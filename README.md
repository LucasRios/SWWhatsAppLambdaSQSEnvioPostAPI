# SW WhatsApp Outbound Executor

Este componente é o "braço operacional" do envio de mensagens. Ele processa pedidos de envio, trata requisitos específicos de mídias para parceiros (Chakra) e executa a chamada final de API.

## 🚀 Fluxo de Trabalho

1. **Entrada**: Recebe da fila SQS um objeto contendo URL de destino, Headers e o Body JSON da mensagem.
2. **Processamento de Mídia (Chakra)**: 
   - Identifica se a URL pertence à API Chakra.
   - Caso haja mídia, realiza o download do arquivo original.
   - Faz o upload do arquivo para o endpoint `/upload-public-media` da Chakra.
   - Atualiza o JSON da mensagem com a nova URL pública da mídia.
3. **Execução**: Realiza o POST HTTP para o provedor de WhatsApp (Meta ou Whapi).
4. **Callback**: Envia o resultado da tentativa (HTTP Status e Resposta) para uma fila de retorno via SQS.

## 🛠️ Tecnologias

- **AWS Lambda** (.NET Core)
- **HttpClient**: Para downloads de mídia e chamadas de API.
- **Regex**: Para extração dinâmica de IDs de plugins das URLs.
- **System.Text.Json**: Manipulação dinâmica de árvores JSON (`JsonNode`).

## 📋 Pré-requisitos

- Fila SQS de entrada configurada.
- Fila SQS de retorno (`whatsapp-outbound-return-queue`) criada.
- Permissões de IAM para `sqs:SendMessage` na fila de retorno.

## ⚙️ Configuração

A URL da fila de retorno está fixada na constante `RETURN_QUEUE_URL`. Para ambientes de produção, recomenda-se mover para uma **Environment Variable**.

## 🧩 Detalhes de Implementação

O método `AdicionarHeaders` é vital pois permite que o sistema seja agnóstico ao provedor, injetando tokens de autorização dinamicamente a partir da string armazenada no banco de dados.
