FROM mcr.microsoft.com/dotnet/sdk:8.0@sha256:306301580fcaa5b445180e759db59309979002d1000669cb4cf58a567d0014bc AS build
WORKDIR /src
COPY CtiDashboard.csproj .
RUN dotnet restore CtiDashboard.csproj
COPY . .
RUN dotnet publish CtiDashboard.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0@sha256:b0beb9cc1dee1c1b0749796110d4734292071b814207ad0d4f40611f7db04f7b AS final
WORKDIR /app
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "CtiDashboard.dll"]
