# [<img src="https://raw.githubusercontent.com/EntexInteractive/Vertex/master/Vertex.Cli/vertex-icon.ico" alt="Vertex Icon" width="32" height="32">](https://github.com/EntexInteractive/Vertex) Vertex

[![.NET](https://img.shields.io/github/actions/workflow/status/EntexInteractive/Vertex/build-latest.yml?label=Main%20build&style=for-the-badge)](https://github.com/EntexInteractive/Vertex/actions/workflows/build-latest.yml) [![latest version](https://img.shields.io/github/v/release/EntexInteractive/Vertex?label=Latest%20release&style=for-the-badge)](https://github.com/EntexInteractive/Vertex/releases/latest) [![GitHub Downloads](https://img.shields.io/github/downloads/EntexInteractive/Vertex/total?style=for-the-badge)](https://github.com/EntexInteractive/Vertex/releases/latest) [![Docker Pulls](https://img.shields.io/docker/pulls/EntexInteractive/Vertex?style=for-the-badge)
]()

## What is Vertex?

Vertex is a server-centric, versioned file synchronization platform that gives you one global repository for your files and lets you selectively synchronize that repository across your devices. Instead of each computer having its own independent copy of your files, Vertex maintains a central repository that belongs to your account. 

You install the Vertex Agent on your computers and connect them to your Vertex account. From the web interface, you decide which parts of your repository each device should synchronize.

## Server configuration

The server is configured entirely through environment variables. It checks them at startup and exits with a list of
every missing or invalid setting.

| Variable                 | Required | Default                    | Description                                                                                              |
|--------------------------|----------|----------------------------|----------------------------------------------------------------------------------------------------------|
| `VX_HTTP_PORT`           | No       | `5000`                     | Port the server listens on. Clear-text HTTP/2 only, unless TLS is configured below.                      |
| `VX_POSTGRES_CONNECTION` | **Yes**  | —                          | Npgsql connection string, e.g. `Host=postgres;Port=5432;Database=vertex;Username=...;Password=...`.      |
| `VX_REDIS_CONNECTION`    | No       | `redis:6379`               | StackExchange.Redis connection string. The server runs with a memory-only cache if Redis is unreachable. |
| `VX_S3_ENDPOINT`         | No       | `https://s3.amazonaws.com` | S3 or S3-compatible endpoint URL (e.g. MinIO, RustFS). Path-style addressing is used.                    |
| `VX_S3_REGION`           | No       | `us-east-1`                | S3 region.                                                                                               |
| `VX_S3_BUCKET`           | No       | `vertex-storage`           | Bucket for file chunks and manifests.                                                                    |
| `VX_S3_ACCESS_KEY`       | No       | —                          | Static S3 access key. Set together with `VX_S3_SECRET_KEY`, or leave both unset.                         |
| `VX_S3_SECRET_KEY`       | No       | —                          | Static S3 secret key.                                                                                    |
| `VX_TLS_CERT`            | No       | —                          | PEM certificate file. With `VX_TLS_KEY`, enables TLS and serves both HTTP/1.1 and HTTP/2 on the port.    |
| `VX_TLS_KEY`             | No       | —                          | PEM private key file for `VX_TLS_CERT`.                                                                  |

When the S3 keys are unset, the AWS SDK default credential chain is used (`AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY`,
EKS IRSA or pod identity, instance profile). In Kubernetes, prefer workload identity, or load the keys from a Secret.
Standard ASP.NET Core variables such as `ASPNETCORE_ENVIRONMENT` also apply; `Development` enables Swagger and gRPC
reflection.

For local development, set these in `Vertex.Server/Properties/launchSettings.json` or with `dotnet user-secrets`.
Keep credentials out of source control.

## Contact

For questions and ideas, reach out via our [GitHub Issues](https://github.com/EntexInteractive/Vertex/issues).
