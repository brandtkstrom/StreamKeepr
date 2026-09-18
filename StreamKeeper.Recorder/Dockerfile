FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["StreamKeeper.Recorder/StreamKeeper.Recorder.csproj", "StreamKeeper.Recorder/"]
RUN dotnet restore "StreamKeeper.Recorder/StreamKeeper.Recorder.csproj"
COPY . .
WORKDIR "/src/StreamKeeper.Recorder"
RUN dotnet build "./StreamKeeper.Recorder.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./StreamKeeper.Recorder.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "StreamKeeper.Recorder.dll"]
