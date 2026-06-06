# LlamaWorkaround

Simple reverse proxy for llama.cpp.  
This is a workaround for the issue described in the following GitHub issue:  
https://github.com/ggml-org/llama.cpp/issues/21678

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
  "SerialRequests": {
    "TargetPaths": [    // The request paths to limit concurrency. Default is the paths of OpenAI compatible API.
      "/v1/chat/completions",
      "/v1/responses",
      "/v1/completions",
      "/v1/embeddings",
      "/v1/messages"
    ],
    "Concurrency": 1    // The maximum concurrency for the specified request paths. Must be less or equal to --models-max of llama.cpp.
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