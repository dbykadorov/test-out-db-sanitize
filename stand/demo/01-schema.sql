-- Демонстрационная база: маленькая, но со всеми ловушками сразу.
--
-- Что здесь специально сделано неудобным, чтобы решение было проверяемым:
--   * внешние ключи, в том числе на текстовый ключ (email) и составной;
--   * UNIQUE на колонке с ПДн - проверка, что замены не коллидируют (F-10);
--   * дата рождения типом date - нетекстовые ПДн, которые легко пропустить;
--   * jsonb с ПДн внутри - данные в чужом формате;
--   * свободный текст с вкраплениями ПДн - самый тяжёлый случай (F-4a);
--   * колонка client_code, в имени которой нет намёка на паспорт, - ловушка
--     для наивной разметки по именам: имя молчит, комментарий говорит,
--     значения имеют формат;
--   * колонка department - НЕ ПДн, и её замена была бы ложным срабатыванием;
--   * CHECK-ограничение на clients.marital_status - конечный домен с ПДн (Р-3):
--     замена обязана остаться внутри домена, иначе восстановление упадёт
--     на ограничении, и при этом не смеет оставить значение собой;
--   * CHECK на orders.status - конечный домен БЕЗ ПДн: он не заменяется вовсе,
--     и это проверка на ложное срабатывание.

CREATE TABLE clients (
    id            serial PRIMARY KEY,
    email         varchar(120) NOT NULL UNIQUE,
    full_name     varchar(150) NOT NULL,
    phone         varchar(20),
    inn           char(12),
    snils         varchar(14),
    client_code   varchar(20),
    birth_date    date,
    city          varchar(80),
    address       text,
    profile       jsonb,
    marital_status varchar(16),
    segment       varchar(20) NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT clients_marital_status_check
        CHECK (marital_status IN ('single', 'married', 'divorced', 'widowed'))
);

CREATE TABLE managers (
    id         serial PRIMARY KEY,
    full_name  varchar(150) NOT NULL,
    email      varchar(120) NOT NULL UNIQUE,
    department varchar(80) NOT NULL
);

CREATE TABLE orders (
    id           serial PRIMARY KEY,
    client_email varchar(120) NOT NULL REFERENCES clients(email),
    manager_id   integer REFERENCES managers(id),
    amount       numeric(12,2) NOT NULL,
    status       varchar(20) NOT NULL,
    comment      text,
    created_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT orders_status_check
        CHECK (status IN ('new', 'paid', 'shipped', 'closed', 'cancelled'))
);

CREATE TABLE order_items (
    order_id  integer NOT NULL REFERENCES orders(id),
    line_no   integer NOT NULL,
    sku       varchar(40) NOT NULL,
    qty       integer NOT NULL,
    PRIMARY KEY (order_id, line_no)
);

CREATE TABLE support_tickets (
    id           serial PRIMARY KEY,
    client_email varchar(120) NOT NULL REFERENCES clients(email),
    subject      varchar(200),
    body         text,
    created_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX orders_client_email_idx ON orders (client_email);
CREATE INDEX support_tickets_client_email_idx ON support_tickets (client_email);

COMMENT ON COLUMN clients.client_code IS 'Серия и номер паспорта';
COMMENT ON COLUMN clients.segment IS 'Сегмент клиента для отчётности, не ПДн';
COMMENT ON COLUMN clients.marital_status IS 'Семейное положение';
COMMENT ON TABLE clients IS 'Клиенты';
