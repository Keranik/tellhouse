# Build context is the relay directory.
# The relay holds no keys and opens nothing; the registry
# carries token hashes only.
FROM python:3.12-slim

WORKDIR /app

COPY src/ /app/src/
COPY demo-seed/ /app/demo-seed/
COPY entrypoint.sh /app/entrypoint.sh

RUN chmod +x /app/entrypoint.sh

EXPOSE 8790

CMD ["/app/entrypoint.sh"]
