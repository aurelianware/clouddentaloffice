FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["src/Shared/CloudDentalOffice.Contracts/CloudDentalOffice.Contracts.csproj", "src/Shared/CloudDentalOffice.Contracts/"]
COPY ["src/Services/VisionService/VisionService.csproj", "src/Services/VisionService/"]
RUN dotnet restore "src/Services/VisionService/VisionService.csproj"
COPY . .
WORKDIR "/src/src/Services/VisionService"
RUN dotnet build "VisionService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "VisionService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
EXPOSE 5108
ENTRYPOINT ["dotnet", "VisionService.dll"]
