-- База управления. Данных здесь нет и быть не может: только заявки, статусы,
-- паспорта и аудит. Это то самое утверждение раздела 2 архитектуры, которое
-- на стенде подкреплено ещё и сетью: контур управления не подключён к сети
-- плоскости данных.

-- Реестр источников и приёмников: ТОЛЬКО метаданные.
--
-- Строк подключения здесь нет и быть не может: контур управления доступа
-- к данным не имеет вовсе (раздел 2 архитектуры). Заявка называет источник
-- идентификатором; учётные данные к нему воркер берёт сам, из каталога
-- в плоскости данных. В боевом контуре этот реестр синхронизируется
-- с хранилищем секретов, а не дублирует его.
-- Регистрация ресурса состоит из ДВУХ шагов, и это не неудобство, а граница
-- зон. Оператор объявляет ресурс здесь - именем и видом. Подключение к нему
-- заводится отдельно, в плоскости данных, куда контур управления не ходит.
--
-- Чтобы шаги не разъехались молча, воркер сам сообщает, какие ресурсы он
-- в состоянии обслужить: колонка available_at обновляется его объявлением.
-- Заявка на ресурс, объявленный, но недоступный воркеру, отклоняется сразу,
-- а не падает в середине прогона.
CREATE TABLE sources (
    id            text PRIMARY KEY,
    title         text NOT NULL,
    kind          text NOT NULL,
    enabled       boolean NOT NULL DEFAULT true,
    registered_by text NOT NULL DEFAULT 'начальная настройка',
    registered_at timestamptz NOT NULL DEFAULT now(),
    available_at  timestamptz,
    CONSTRAINT sources_kind_check CHECK (kind IN ('connection', 'dump'))
);

CREATE TABLE sinks (
    id            text PRIMARY KEY,
    title         text NOT NULL,
    kind          text NOT NULL,
    enabled       boolean NOT NULL DEFAULT true,
    registered_by text NOT NULL DEFAULT 'начальная настройка',
    registered_at timestamptz NOT NULL DEFAULT now(),
    available_at  timestamptz,
    CONSTRAINT sinks_kind_check CHECK (kind IN ('database', 'dump'))
);

INSERT INTO sources (id, title, kind) VALUES
    ('demo-prod', 'Демонстрационная прод-реплика', 'connection');

INSERT INTO sinks (id, title, kind) VALUES
    ('demo-sanitary', 'Санитарная база стенда', 'database');

CREATE TABLE requests (
    id            bigserial PRIMARY KEY,
    run_id        text NOT NULL UNIQUE,
    requested_by  text NOT NULL,
    purpose       text NOT NULL,
    source_id     text NOT NULL REFERENCES sources(id),
    sink_id       text NOT NULL REFERENCES sinks(id),
    status        text NOT NULL DEFAULT 'queued',
    created_at    timestamptz NOT NULL DEFAULT now(),
    started_at    timestamptz,
    finished_at   timestamptz,
    publishable   boolean,
    passport      jsonb,
    error         text,
    CONSTRAINT requests_status_check
        CHECK (status IN ('queued', 'running', 'done', 'failed', 'rejected'))
);

-- Право на выгрузку выдаётся конкретному получателю по конкретной заявке.
-- Ссылки со сроком жизни здесь нет намеренно: она предъявительская,
-- и скачает её кто угодно (раздел 8).
CREATE TABLE grants (
    id          bigserial PRIMARY KEY,
    request_id  bigint NOT NULL REFERENCES requests(id),
    recipient   text NOT NULL,
    granted_by  text NOT NULL,
    granted_at  timestamptz NOT NULL DEFAULT now(),
    revoked_at  timestamptz,
    UNIQUE (request_id, recipient)
);

CREATE TABLE audit (
    id       bigserial PRIMARY KEY,
    at       timestamptz NOT NULL DEFAULT now(),
    actor    text NOT NULL,
    action   text NOT NULL,
    subject  text NOT NULL,
    detail   text NOT NULL DEFAULT ''
);

CREATE INDEX audit_at_idx ON audit (at DESC);
CREATE INDEX requests_status_idx ON requests (status);

-- Заглушка провайдера идентичности: статический список вместо OIDC.
-- Что теряется - проверка подписи токена, отзыв на стороне провайдера,
-- единый вход. Что остаётся - разграничение ролей и привязка выдачи
-- к заявке. Полный перечень потерь - в README.
CREATE TABLE identities (
    subject  text PRIMARY KEY,
    role     text NOT NULL,
    CONSTRAINT identities_role_check
        CHECK (role IN ('owner', 'operator', 'consumer'))
);

INSERT INTO identities (subject, role) VALUES
    ('owner@outtech.example', 'owner'),
    ('operator@outtech.example', 'operator'),
    ('analyst@outtech.example', 'consumer'),
    ('vendor@partner.example', 'consumer');
