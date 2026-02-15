FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["src/Services/ApiGateway/ApiGateway.csproj", "src/Services/ApiGateway/"]
RUN dotnet restore "src/Services/ApiGateway/ApiGateway.csproj"
COPY . .
WORKDIR "/src/src/Services/ApiGateway"
RUN dotnet build "ApiGateway.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ApiGateway.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV ASPNETCORE_URLS=http://+:5200
EXPOSE 5200
ENTRYPOINT ["dotnet", "ApiGateway.dll"]
