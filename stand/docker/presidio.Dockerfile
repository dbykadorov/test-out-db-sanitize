# Детектор ПДн: Presidio с русской моделью spaCy.
#
# Базовый образ умеет только английский: запрос с language=ru отвергается
# с «No matching recognizers were found». Для русской базы это означало бы,
# что имена в свободном тексте не находятся вовсе, а разбор текста - самая
# дорогая и самая важная часть конвейера.
FROM mcr.microsoft.com/presidio-analyzer:latest

USER root

# Версия модели выбрана под spaCy 3.8 из базового образа: несовместимая
# модель не загрузится, и сервис поднимется без русского языка молча.
RUN pip install --no-cache-dir \
    https://github.com/explosion/spacy-models/releases/download/ru_core_news_sm-3.8.0/ru_core_news_sm-3.8.0-py3-none-any.whl

COPY stand/docker/presidio/nlp.yaml         /app/conf/nlp.yaml
COPY stand/docker/presidio/analyzer.yaml    /app/conf/analyzer.yaml
COPY stand/docker/presidio/recognizers.yaml /app/conf/recognizers.yaml

ENV NLP_CONF_FILE=conf/nlp.yaml \
    ANALYZER_CONF_FILE=conf/analyzer.yaml \
    RECOGNIZER_REGISTRY_CONF_FILE=conf/recognizers.yaml
