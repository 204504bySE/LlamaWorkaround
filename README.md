# LlamaWorkaround

Simple reverse proxy for llama.cpp.  
This is a workaround for the issue described in the following GitHub issue:  
https://github.com/open-webui/open-webui/discussions/26200

## Environment

- ASP.NET Core 10.0

## Usage

### 1. Build the application

Build on Visual Studio or using the .NET CLI.

### 2. Edit the configuration file

Edit the following in `appsettings.json`.

```json
{
  "Hosting": {
    "Urls": [ "http://127.0.0.1:12345" ] // The URL and port to listen on
  },
  "SseBatching": {
    "DestinationAddress": "http://127.0.0.1:23456/", // The URL and port of the llama.cpp server
    "ChunkCount": 4 // Combine this many OpenAI streaming chunks into one SSE message.
  },
  "ReverseProxy": {
    "Routes": {
      "all_route": {
        "ClusterId": "cluster1",
        "Match": {
          "Path": "/{**catch-all}"
        }
      }
    },
    "Clusters": {
      "cluster1": {
        "Destinations": {
          "destination1": {
            "Address": "http://127.0.0.1:23456/"    // The URL and port of the llama.cpp server
          }
        }
      }
    }
  }
}
```

`/v1/chat/completions` is handled separately from the catch-all YARP route. When the upstream response is `text/event-stream`, consecutive OpenAI chunks are merged by `choices[].index`; `delta.content`, `delta.reasoning_content`, and `delta.refusal` are concatenated. The final partial batch is sent before `[DONE]`. Non-SSE responses and other paths are proxied unchanged. Keep `SseBatching:DestinationAddress` and the YARP destination pointed at the same server.

### 3. Run the application

```bash
dotnet LlamaWorkaround.dll
```

### 4. Create a systemd service file

```ini
[Unit]
Description=Workaround for llama.cpp server
After=network-online.target docker.service  //docker.service is required if Hosting.Urls is set to listen docker network interface.

[Service]
Type=simple
ExecStart=/usr/bin/dotnet /home/llama/LlamaWorkaround/LlamaWorkaround.dll   // Adjust the path to the application as needed
WorkingDirectory=/home/llama/LlamaWorkaround    // Also adjust it.
[Install]
WantedBy=multi-user.target
```