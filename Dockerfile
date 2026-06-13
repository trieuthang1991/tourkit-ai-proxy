FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
# Cài Node 20 LTS để MSBuild target BuildFrontendBundle chạy được `npx esbuild`
# (xem TourkitAiProxy.csproj — Target chỉ fire ở Configuration=Release).
RUN curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TourkitAiProxy.dll"]
