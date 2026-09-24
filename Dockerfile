# Build and test in the SDK image; ship only the runtime. The test stage means an image
# cannot be built from code whose tests fail.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY VoiceAgent.sln ./
COPY src/VoiceAgent/VoiceAgent.csproj src/VoiceAgent/
COPY tests/VoiceAgent.Tests/VoiceAgent.Tests.csproj tests/VoiceAgent.Tests/
RUN dotnet restore
COPY . .
RUN dotnet test -c Release --no-restore
RUN dotnet publish src/VoiceAgent -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=0
EXPOSE 8080
USER app
# Liveness is GET /healthz; the orchestrator (Cloud Run, Kubernetes) probes it over HTTP.
ENTRYPOINT ["dotnet", "VoiceAgent.dll"]
