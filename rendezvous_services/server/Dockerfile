FROM python:3.11-slim

ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1

WORKDIR /app

RUN pip install --no-cache-dir \
    "fastapi>=0.115,<1.0" \
    "pydantic>=2.7,<3.0" \
    "uvicorn[standard]>=0.30,<1.0" \
    "miniupnpc>=2.2.8,<3.0"

COPY . /app

EXPOSE 49920

CMD ["uvicorn", "server.main:app", "--host", "0.0.0.0", "--port", "49920"]