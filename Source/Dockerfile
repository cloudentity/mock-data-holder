FROM mcr.microsoft.com/dotnet/aspnet:5.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 8002

FROM mcr.microsoft.com/dotnet/sdk:5.0 AS build
WORKDIR /src

COPY . ./

FROM build AS publish

COPY ./CDR.DataHolder.API.Infrastructure/. /app/CDR.DataHolder.API.Infrastructure
COPY ./CDR.DataHolder.Repository/. /app/CDR.DataHolder.Repository
COPY ./CDR.DataHolder.Domain/. /app/CDR.DataHolder.Domain
COPY ./CDR.DataHolder.Admin.API/. /app/CDR.DataHolder.Admin.API
COPY ./CDR.DataHolder.Manage.API/. /app/CDR.DataHolder.Manage.API
COPY ./CDR.DataHolder.Public.API/. /app/CDR.DataHolder.Public.API
COPY ./CDR.DataHolder.Resource.API/. /app/CDR.DataHolder.Resource.API
COPY ./CDR.DataHolder.IdentityServer/. /app/CDR.DataHolder.IdentityServer
COPY ./CDR.DataHolder.API.Gateway.mTLS/. /app/CDR.DataHolder.API.Gateway.mTLS

WORKDIR /app/CDR.DataHolder.Admin.API
RUN dotnet publish -c Release -o /app/publish/admin
WORKDIR /app/CDR.DataHolder.Manage.API
RUN dotnet publish -c Release -o /app/publish/manage
WORKDIR /app/CDR.DataHolder.Public.API
RUN dotnet publish -c Release -o /app/publish/public
WORKDIR /app/CDR.DataHolder.Resource.API
RUN dotnet publish -c Release -o /app/publish/resource
WORKDIR /app/CDR.DataHolder.IdentityServer
RUN dotnet publish -c Release -o /app/publish/idsvr
WORKDIR /app/CDR.DataHolder.API.Gateway.mTLS
RUN dotnet publish -c Release -o /app/publish/gateway-mtls

COPY supervisord.conf /app/publish/supervisord.conf

FROM base AS final
WORKDIR /app

RUN apt-get update && apt-get install -y supervisor

RUN apt-get update && apt-get install -y sudo

COPY --from=publish /app/publish/supervisord.conf .
COPY --from=publish /app/publish/resource ./resource
COPY --from=publish /app/publish/admin ./admin
COPY --from=publish /app/publish/manage ./manage
COPY --from=publish /app/publish/idsvr ./idsvr
COPY --from=publish /app/publish/gateway-mtls ./gateway-mtls
COPY --from=publish /app/publish/public ./public

RUN sudo cp ./gateway-mtls/Certificates/ca.crt /usr/local/share/ca-certificates/ca.crt

RUN sudo update-ca-certificates

ENV ASPNETCORE_URLS=https://+:7000;https://+:7001

ENTRYPOINT ["/usr/bin/supervisord", "-c", "/app/supervisord.conf"]
