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
    "Urls": ["http://127.0.0.1:12345"] // The URL and port to listen on
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
            "Address": "http://127.0.0.1:23456/" // The URL and port of the llama.cpp server
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

## Usage (with Docker)

The repository includes a multi-stage `Dockerfile`. The container listens on port `12435` (the `HOSTING__URLS__0` environment variable overrides `Hosting:Urls` in `appsettings.json`).

### 1. Build the image

```bash
docker build -t llamaworkaround:latest .
```

### 2. Run the container

If the llama.cpp server runs on the host, use the host network so the container can reach `127.0.0.1:12434`:

```bash
docker run -d --name llamaworkaround --network host llamaworkaround:latest
```

If the llama.cpp server runs in another container or on another host, override the destination addresses with environment variables:

```bash
docker run -d --name llamaworkaround -p 12435:12435 \
  -e SSEBATCHING__DESTINATIONADDRESS=http://llama-server:12434/ \
  -e REVERSEPROXY__CLUSTERS__CLUSTER1__DESTINATIONS__DESTINATION1__ADDRESS=http://llama-server:12434/ \
  llamaworkaround:latest
```

Both `SseBatching:DestinationAddress` and the YARP destination must point at the same llama.cpp server.
