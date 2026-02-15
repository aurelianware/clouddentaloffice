FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["src/Shared/CloudDentalOffice.Contracts/CloudDentalOffice.Contracts.csproj", "src/Shared/CloudDentalOffice.Contracts/"]
COPY ["src/Shared/CloudDentalOffice.EdiCommon/CloudDentalOffice.EdiCommon.csproj", "src/Shared/CloudDentalOffice.EdiCommon/"]
COPY ["src/Services/EligibilityService/EligibilityService.csproj", "src/Services/EligibilityService/"]
RUN dotnet restore "src/Services/EligibilityService/EligibilityService.csproj"
COPY . .
WORKDIR "/src/src/Services/EligibilityService"
RUN dotnet build "EligibilityService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "EligibilityService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV ASPNETCORE_URLS=http://+:5104
EXPOSE 5104
ENTRYPOINT ["dotnet", "EligibilityService.dll"]
