FROM node:22-bookworm-slim AS ui
WORKDIR /ui
COPY src/DuckNet.DashboardCenter/ui/package.json src/DuckNet.DashboardCenter/ui/package-lock.json ./
RUN npm ci
COPY src/DuckNet.DashboardCenter/ui/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
COPY --from=ui /wwwroot /src/src/DuckNet.DashboardCenter/wwwroot
RUN dotnet publish src/DuckNet.DashboardCenter/DuckNet.DashboardCenter.csproj -c Release -o /app -p:SkipDashboardUi=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
ENV EVENT_LOG_URL=http://127.0.0.1:1
EXPOSE 8080
ENTRYPOINT ["dotnet", "DuckNet.DashboardCenter.dll"]
