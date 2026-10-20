# syntax=docker/dockerfile:1
FROM node:26-bookworm-slim AS build
WORKDIR /app
ARG GITHUB_SHA
ENV NEXT_PUBLIC_BUILD_COMMIT=${GITHUB_SHA}
ENV NEXT_TELEMETRY_DISABLED=1
COPY JerryChart.Web/package.json JerryChart.Web/package-lock.json ./
COPY JerryChart.Web/tooling/lint/package.json JerryChart.Web/tooling/lint/package-lock.json ./tooling/lint/
RUN npm ci --no-fund
COPY JerryChart.Web/ ./
RUN npm run build

FROM node:26-bookworm-slim AS runtime
WORKDIR /app
ENV NODE_ENV=production \
    NEXT_TELEMETRY_DISABLED=1 \
    HOSTNAME=0.0.0.0 \
    PORT=8080
COPY --from=build --chown=node:node /app/.next/standalone/ ./
COPY --from=build --chown=node:node /app/.next/static/ ./.next/static/
COPY --from=build --chown=node:node /app/public/ ./public/
USER node
EXPOSE 8080
CMD ["node", "server.js"]
