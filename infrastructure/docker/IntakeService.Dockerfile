FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["src/Shared/CloudDentalOffice.Contracts/CloudDentalOffice.Contracts.csproj", "src/Shared/CloudDentalOffice.Contracts/"]
COPY ["src/Shared/CloudDentalOffice.Messaging/CloudDentalOffice.Messaging.csproj", "src/Shared/CloudDentalOffice.Messaging/"]
COPY ["src/Services/IntakeService/IntakeService.csproj", "src/Services/IntakeService/"]
RUN dotnet restore "src/Services/IntakeService/IntakeService.csproj"
COPY . .
WORKDIR "/src/src/Services/IntakeService"
RUN dotnet build "IntakeService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "IntakeService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV ASPNETCORE_URLS=http://+:5109
EXPOSE 5109
ENTRYPOINT ["dotnet", "IntakeService.dll"]
