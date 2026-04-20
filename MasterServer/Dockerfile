FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY MasterServer/ ./MasterServer/
COPY Shared/ ./Shared/
WORKDIR /src/MasterServer
RUN dotnet restore
RUN dotnet publish -c Release -o /publish

FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine
WORKDIR /app
COPY --from=build /publish .
ENV MASTER_SERVER_PORT=14343
ENV HOST_TIMEOUT_SECONDS=30
ENV LOG_MODE=player
ENV DATA_DIRECTORY=data
ENV SERVER_DISPLAY_NAME="Apotheon Master Server"
EXPOSE 14343/udp
ENTRYPOINT ["dotnet", "MasterServer.dll"]
