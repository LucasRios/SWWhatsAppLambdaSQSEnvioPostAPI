using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SQS;
using Amazon.SQS.Model;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace WhatsAppLambdaOutbound
{
    public class OutboundExecutor
    {
        // Reuso de instâncias para performance
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly IAmazonSQS _sqsClient = new AmazonSQSClient();

        // Fila que receberá o status final para ser gravado no SQL por outra Lambda
        private const string RETURN_QUEUE_URL = "<RETURN_QUEUE_URL>";

        public async Task FunctionHandler(SQSEvent evnt, ILambdaContext context)
        {
            if (evnt?.Records == null) return;

            foreach (var record in evnt.Records)
            {
                if (string.IsNullOrWhiteSpace(record.Body)) continue;

                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                OutboundData msgData = null;

                try
                {
                    msgData = JsonSerializer.Deserialize<OutboundData>(record.Body, jsonOptions);
                }
                catch (Exception ex)
                {
                    context.Logger.LogLine($"Erro deserialização: {ex.Message}");
                    continue;
                }

                var result = new OutboundResult { CodSysFilaEnvioMensagens = msgData.CodSysFilaEnvioMensagens };

                try
                {
                    // --- LÓGICA DE INTERCEPTAÇÃO DE MÍDIA CHAKRA/META ---
                    // Se for para a API Chakra e contiver mídia, precisa fazer o upload do binário primeiro
                    if (msgData.Url.Contains("chakrahq.com") && EMediaParaUpload(msgData.Body))
                    {
                        context.Logger.LogLine("Mídia detectada para Chakra. Iniciando pré-upload...");
                        msgData.Body = await ProcessarUploadMidiaChakra(msgData, context);
                    }

                    // --- ENVIO DA MENSAGEM FINAL ---
                    using var request = new HttpRequestMessage(HttpMethod.Post, msgData.Url);

                    // Adiciona Headers dinâmicos (como Authorization Bearer) vindos do banco
                    AdicionarHeaders(request, msgData.Header);

                    request.Content = new StringContent(msgData.Body ?? "{}", Encoding.UTF8, "application/json");

                    var response = await _httpClient.SendAsync(request);
                    result.ResponseContent = await response.Content.ReadAsStringAsync();

                    // Status 3 indica 'Enviado/Processado' no seu sistema, -100 indica erro
                    result.Status = response.IsSuccessStatusCode ? 3 : -100;
                }
                catch (Exception ex)
                {
                    context.Logger.LogLine($"Erro no processamento ID {msgData.CodSysFilaEnvioMensagens}: {ex.Message}");
                    result.Status = -100;
                    result.ResponseContent = ex.Message;
                }

                // Envia o resultado para a próxima fila (SaveReturn)
                await EnviarRetornoSQS(result, context);
            }
        }

        /// <summary>
        /// Verifica se o payload JSON da mensagem contém tags de mídia
        /// </summary>
        private bool EMediaParaUpload(string body)
        {
            if (string.IsNullOrEmpty(body)) return false;
            return body.Contains("\"image\"") || body.Contains("\"video\"") || body.Contains("\"audio\"") || body.Contains("\"document\"");
        }

        /// <summary>
        /// Realiza o fluxo complexo de: Baixar da origem -> Postar Binário na Chakra -> Pegar nova URL
        /// </summary>
        private async Task<string> ProcessarUploadMidiaChakra(OutboundData data, ILambdaContext context)
        {
            try
            {
                var node = JsonNode.Parse(data.Body);
                string type = node["type"]?.ToString();
                if (string.IsNullOrEmpty(type)) return data.Body;

                var mediaNode = node[type];
                string linkOriginal = mediaNode?["link"]?.ToString();

                if (string.IsNullOrEmpty(linkOriginal) || !linkOriginal.StartsWith("http"))
                    return data.Body;

                // 1. Extrair ID do Plugin da URL de destino usando Expressão Regular
                var match = Regex.Match(data.Url, @"whatsapp/([^/]+)");
                if (!match.Success) return data.Body;

                string pluginId = match.Groups[1].Value;
                string uploadUrl = $"https://api.chakrahq.com/v1/ext/plugin/whatsapp/{pluginId}/upload-public-media";

                // 2. Download da mídia da URL temporária/original
                byte[] fileBytes = await _httpClient.GetByteArrayAsync(linkOriginal);
                string fileName = type == "document" ? (mediaNode["filename"]?.ToString() ?? "file.pdf") : "file.bin";

                // 3. Montagem do formulário Multipart (necessário para upload de arquivos)
                using var multipartContent = new MultipartFormDataContent();
                var fileContent = new ByteArrayContent(fileBytes);
                fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");

                multipartContent.Add(fileContent, "file", fileName);
                multipartContent.Add(new StringContent(fileName), "filename");

                using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
                AdicionarHeaders(uploadRequest, data.Header);
                uploadRequest.Content = multipartContent;

                var uploadResponse = await _httpClient.SendAsync(uploadRequest);
                var uploadResultJson = await uploadResponse.Content.ReadAsStringAsync();

                if (uploadResponse.IsSuccessStatusCode)
                {
                    // Faz o parse do retorno da Chakra para obter a nova URL pública gerada
                    using var doc = JsonDocument.Parse(uploadResultJson);
                    if (doc.RootElement.TryGetProperty("_data", out var dataNode) &&
                        dataNode.TryGetProperty("publicMediaUrl", out var urlNode))
                    {
                        string novoLink = urlNode.GetString();
                        mediaNode["link"] = novoLink; // Substitui o link no JSON original
                        context.Logger.LogLine($"[SUCCESS] Upload Chakra realizado com sucesso.");
                    }
                }

                return node.ToJsonString();
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"[FATAL] Erro fluxo upload: {ex.Message}");
                return data.Body;
            }
        }

        /// <summary>
        /// Transforma uma string formatada "Key:Value;Key2:Value2" em Headers do HttpClient
        /// </summary>
        private void AdicionarHeaders(HttpRequestMessage request, string headerStr)
        {
            if (string.IsNullOrWhiteSpace(headerStr)) return;

            var headersArray = headerStr.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var headerItem in headersArray)
            {
                var parts = headerItem.Split(':', 2);
                if (parts.Length == 2)
                {
                    var name = parts[0].Trim();
                    var value = parts[1].Trim();
                    // O Content-Type é definido no StringContent, não pode ser adicionado manualmente no Header
                    if (!name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Headers.TryAddWithoutValidation(name, value);
                    }
                }
            }
        }

        private async Task EnviarRetornoSQS(OutboundResult result, ILambdaContext context)
        {
            try
            {
                await _sqsClient.SendMessageAsync(new SendMessageRequest
                {
                    QueueUrl = RETURN_QUEUE_URL,
                    MessageBody = JsonSerializer.Serialize(result)
                });
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"Erro SQS Return: {ex.Message}");
            }
        }

        // Classes de Modelo
        public class OutboundData
        {
            public long CodSysFilaEnvioMensagens { get; set; }
            public string Url { get; set; }
            public string Header { get; set; }
            public string Body { get; set; }
            public string Instancia { get; set; }
        }

        public class OutboundResult
        {
            public long CodSysFilaEnvioMensagens { get; set; }
            public int Status { get; set; }
            public string ResponseContent { get; set; }
        }
    }
}