FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble

RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        libicu74 \
        libssl3t64 \
        libstdc++6 \
        libgcc-s1 \
        zlib1g \
        libsnappy1v5 \
        liblz4-1 \
        libzstd1 \
        libbz2-1.0 \
        curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY . /app/
RUN chmod +x /app/Orleans.Durable.Storage.Bench

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080 11111 30000

ENTRYPOINT ["/app/Orleans.Durable.Storage.Bench"]
