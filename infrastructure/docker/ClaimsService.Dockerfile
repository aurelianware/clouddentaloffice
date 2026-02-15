FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["src/Shared/CloudDentalOffice.Contracts/CloudDentalOffice.Contracts.csproj", "src/Shared/CloudDentalOffice.Contracts/"]
COPY ["src/Shared/CloudDentalOffice.EdiCommon/CloudDentalOffice.EdiCommon.csproj", "src/Shared/CloudDentalOffice.EdiCommon/"]
COPY ["src/Services/ClaimsService/ClaimsService.csproj", "src/Services/ClaimsService/"]
RUN dotnet restore "src/Services/ClaimsService/ClaimsService.csproj"
COPY . .
WORKDIR "/src/src/Services/ClaimsService"
RUN dotnet build "ClaimsService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ClaimsService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV ASPNETCORE_URLS=http://+:5103
EXPOSE 5103
ENTRYPOINT ["dotnet", "ClaimsService.dll"]
