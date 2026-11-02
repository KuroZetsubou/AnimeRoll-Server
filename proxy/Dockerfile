# Caddy with the DuckDNS DNS plugin: certificates come from Let's Encrypt via DNS-01,
# so one wildcard certificate covers anime.<name>.duckdns.org, ats.<name>.duckdns.org, …
FROM caddy:2-builder AS build
RUN xcaddy build --with github.com/caddy-dns/duckdns

FROM caddy:2
COPY --from=build /usr/bin/caddy /usr/bin/caddy
