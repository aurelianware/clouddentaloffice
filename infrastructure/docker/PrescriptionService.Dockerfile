FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["src/Shared/CloudDentalOffice.Contracts/CloudDentalOffice.Contracts.csproj", "src/Shared/CloudDentalOffice.Contracts/"]
COPY ["src/Services/PrescriptionService/PrescriptionService.csproj", "src/Services/PrescriptionService/"]
RUN dotnet restore "src/Services/PrescriptionService/PrescriptionService.csproj"
COPY . .
WORKDIR "/src/src/Services/PrescriptionService"
RUN dotnet build "PrescriptionService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "PrescriptionService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
EXPOSE 5107
ENTRYPOINT ["dotnet", "PrescriptionService.dll"]
