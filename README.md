# [<img src="https://raw.githubusercontent.com/EntexInteractive/Vertex/master/Vertex.Cli/vertex-icon.ico" alt="Vertex Icon" width="32" height="32">](https://github.com/EntexInteractive/Vertex) Vertex

[![.NET](https://img.shields.io/github/actions/workflow/status/EntexInteractive/Vertex/build-latest.yml?label=Main%20build&style=for-the-badge)](https://github.com/EntexInteractive/Vertex/actions/workflows/build-latest.yml) [![latest version](https://img.shields.io/github/v/release/EntexInteractive/Vertex?label=Latest%20release&style=for-the-badge)](https://github.com/EntexInteractive/Vertex/releases/latest) [![GitHub Downloads](https://img.shields.io/github/downloads/EntexInteractive/Vertex/total?style=for-the-badge)](https://github.com/EntexInteractive/Vertex/releases/latest) [![Docker Pulls](https://img.shields.io/docker/pulls/EntexInteractive/Vertex?style=for-the-badge)
]()

## What is Vertex?

Vertex is a server-centric, versioned file synchronization platform that gives you one global repository for your files and lets you selectively synchronize that repository across your devices. Instead of each computer having its own independent copy of your files, Vertex maintains a central repository that belongs to your account. 

You install the Vertex Agent on your computers and connect them to your Vertex account. From the web interface, you decide which parts of your repository each device should synchronize.

## S3 storage configuration

S3 settings are stored in `server.yml`; other server settings continue to use `appsettings.json`. The server creates
`server.yml` with defaults on first launch. Its storage section looks like this:

```yaml
storage:
  endpoint: https://s3.amazonaws.com
  region: us-east-1
  bucket: your-bucket
  accessKey: ""
  secretKey: ""
```

The AWS SDK default credential chain is used when `accessKey` and `secretKey` are blank. In Kubernetes, provide
credentials through an AWS workload identity or role rather than embedding access keys in `server.yml`. For
S3-compatible services such as MinIO, set `endpoint` to the service URL; path-style addressing is enabled.

Uploads are streamed as multipart objects. Each active large upload uses temporary pod storage for its current part;
configure enough ephemeral storage for the largest part expected by your workload. AWS S3 itself limits a multipart
object to 10,000 parts of at most 5 GiB each. Add a bucket lifecycle rule to remove incomplete multipart uploads if a
pod stops before the upload can be aborted.

## Contact

For questions and ideas, reach out via our [GitHub Issues](https://github.com/EntexInteractive/Vertex/issues).
