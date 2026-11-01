# Custom Keycloak image — bakes realm JSON + AgentOS.NET theme into the image so neither bind-mounts nor
# WithRealmImport are needed. Used by the cloud deploy (ACA) and the on-prem install (deploy/compose,
# deploy/helm — docs/ONPREM.md). Build stage compiles KC with Postgres support + the health endpoints
# (--optimized startup skips re-build at container start time).
#
# Context: infra/keycloak/  (set by AppHost WithDockerfile("../../infra/keycloak"))
# Files available in context: agentic-realm.json, themes/
#
# KEYCLOAK_IMAGE lets an air-gapped site build from its own registry mirror, e.g.
#   docker build --build-arg KEYCLOAK_IMAGE=registry.corp/keycloak/keycloak:26.6 infra/keycloak

# NOTE: quay.io has no bare major tag — `keycloak:26` does NOT resolve; pin minor.
ARG KEYCLOAK_IMAGE=quay.io/keycloak/keycloak:26.6

FROM ${KEYCLOAK_IMAGE} AS builder
WORKDIR /opt/keycloak

# Build KC with the Postgres DB provider pre-compiled for fast optimized startup. Health checks are a
# build-time option: /health/live + /health/ready on the management port 9000 (container probes).
RUN /opt/keycloak/bin/kc.sh build --db=postgres --health-enabled=true

FROM ${KEYCLOAK_IMAGE}
COPY --from=builder /opt/keycloak/ /opt/keycloak/

# Bake realm so KC imports it on first start (idempotent — KC skips if realm already exists in DB).
COPY agentic-realm.json /opt/keycloak/data/import/agentic-realm.json

# Bake AgentOS.NET login theme.
COPY themes/agentos /opt/keycloak/themes/agentos

ENTRYPOINT ["/opt/keycloak/bin/kc.sh"]
CMD ["start", "--import-realm", "--optimized"]
