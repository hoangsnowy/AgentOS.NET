# Custom Keycloak image for cloud deploy — bakes realm JSON + AgentOS.NET theme into the image so
# neither bind-mounts nor WithRealmImport are needed in ACA. Build stage compiles KC with
# Postgres support (--optimized startup skips re-build at container start time).
#
# Context: infra/keycloak/  (set by AppHost WithDockerfile("../../infra/keycloak"))
# Files available in context: agentic-realm.json, themes/

# NOTE: quay.io has no bare major tag — `keycloak:26` does NOT resolve; pin minor.
FROM quay.io/keycloak/keycloak:26.6 AS builder
WORKDIR /opt/keycloak

# Build KC with Postgres DB provider pre-compiled for fast optimized startup.
RUN /opt/keycloak/bin/kc.sh build --db=postgres

FROM quay.io/keycloak/keycloak:26.6
COPY --from=builder /opt/keycloak/ /opt/keycloak/

# Bake realm so KC imports it on first start (idempotent — KC skips if realm already exists in DB).
COPY agentic-realm.json /opt/keycloak/data/import/agentic-realm.json

# Bake AgentOS.NET login theme.
COPY themes/agentos /opt/keycloak/themes/agentos

ENTRYPOINT ["/opt/keycloak/bin/kc.sh"]
CMD ["start", "--import-realm", "--optimized"]
