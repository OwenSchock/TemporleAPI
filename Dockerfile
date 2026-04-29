# 1. Build Stage: Use the heavy .NET 10 SDK to compile your code
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app

# 2. Serve Stage: Use the lightweight .NET 10 runtime to run the app
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# 3. Tell Render to listen on port 8080
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "TemporleAPI.dll"]