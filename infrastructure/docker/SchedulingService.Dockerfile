FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["src/Shared/CloudDentalOffice.Contracts/CloudDentalOffice.Contracts.csproj", "src/Shared/CloudDentalOffice.Contracts/"]
COPY ["src/Services/SchedulingService/SchedulingService.csproj", "src/Services/SchedulingService/"]
RUN dotnet restore "src/Services/SchedulingService/SchedulingService.csproj"
COPY . .
WORKDIR "/src/src/Services/SchedulingService"
RUN dotnet build "SchedulingService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "SchedulingService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV ASPNETCORE_URLS=http://+:5102
EXPOSE 5102
ENTRYPOINT ["dotnet", "SchedulingService.dll"]
