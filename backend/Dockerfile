FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["AzCostPilot.sln", "."]
COPY ["backend/AzCostPilot.Api.csproj", "backend/"]
COPY ["shared/AzCostPilot.Data/AzCostPilot.Data.csproj", "shared/AzCostPilot.Data/"]
RUN dotnet restore "backend/AzCostPilot.Api.csproj"

COPY . .
RUN dotnet publish "backend/AzCostPilot.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "AzCostPilot.Api.dll"]
