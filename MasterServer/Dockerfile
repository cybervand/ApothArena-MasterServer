FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY MasterServer.csproj .
RUN dotnet restore
COPY Program.cs .
RUN dotnet publish -c Release -o /publish

FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine
WORKDIR /app
COPY --from=build /publish .
EXPOSE 14343/udp
ENTRYPOINT ["dotnet", "MasterServer.dll"]
