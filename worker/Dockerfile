FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["AzCostPilot.sln", "."]
COPY ["worker/AzCostPilot.Worker.csproj", "worker/"]
COPY ["shared/AzCostPilot.Data/AzCostPilot.Data.csproj", "shared/AzCostPilot.Data/"]
RUN dotnet restore "worker/AzCostPilot.Worker.csproj"

COPY . .
RUN dotnet publish "worker/AzCostPilot.Worker.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "AzCostPilot.Worker.dll"]
