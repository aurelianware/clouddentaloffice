FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["src/Shared/CloudDentalOffice.Contracts/CloudDentalOffice.Contracts.csproj", "src/Shared/CloudDentalOffice.Contracts/"]
COPY ["src/CloudDentalOffice.Portal/CloudDentalOffice.Portal.csproj", "src/CloudDentalOffice.Portal/"]
RUN dotnet restore "src/CloudDentalOffice.Portal/CloudDentalOffice.Portal.csproj"
COPY . .
WORKDIR "/src/src/CloudDentalOffice.Portal"
RUN dotnet build "CloudDentalOffice.Portal.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "CloudDentalOffice.Portal.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000
ENTRYPOINT ["dotnet", "CloudDentalOffice.Portal.dll"]
