FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Emage/Emage.csproj", "Emage/"]
RUN dotnet restore "Emage/Emage.csproj"
COPY . .
WORKDIR "/src/Emage"
RUN dotnet build "./Emage.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Emage.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:3233 \
    ASPNETCORE_HTTP_PORTS=3233 \
    Emage__ImageRoot=/data/images
EXPOSE 3233
COPY --from=publish /app/publish .
RUN mkdir -p /data/images && chown -R $APP_UID /data/images
USER $APP_UID
ENTRYPOINT ["dotnet", "Emage.dll"]