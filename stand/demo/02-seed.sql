-- Детерминированный сев демо-базы.
--
-- Случайности здесь нет намеренно: прогон должен быть воспроизводим, а проверка
-- полноты (F-4) сравнивает результат с известными координатами ПДн. Всё выводится
-- арифметикой от номера строки, поэтому база восстанавливается байт в байт.
--
-- Контрольные суммы ИНН и СНИЛС считаются настоящие: если бы данные источника
-- их не проходили, проверка правдоподобности замен ничего бы не значила -
-- сравнивать было бы не с чем.

CREATE SCHEMA demo_util;

CREATE FUNCTION demo_util.pick(items text[], i bigint) RETURNS text
    LANGUAGE sql IMMUTABLE AS
$$ SELECT items[1 + (i % array_length(items, 1))] $$;

CREATE FUNCTION demo_util.inn12(base bigint) RETURNS char(12)
    LANGUAGE plpgsql IMMUTABLE AS
$$
DECLARE
    -- Множитель большой намеренно: при маленьком номер строки просвечивал бы
    -- сквозь идентификатор ведущими нулями, и данные источника перестали бы
    -- быть похожими на настоящие.
    digits text := lpad((1000000000 + (base * 982451653 + 1234567891) % 9000000000)::text, 10, '0');
    w1 int[] := ARRAY[7, 2, 4, 10, 3, 5, 9, 4, 6, 8];
    w2 int[] := ARRAY[3, 7, 2, 4, 10, 3, 5, 9, 4, 6, 8];
    s int := 0;
    d11 int;
    d12 int;
BEGIN
    FOR i IN 1..10 LOOP
        s := s + substr(digits, i, 1)::int * w1[i];
    END LOOP;
    d11 := s % 11 % 10;

    digits := digits || d11::text;
    s := 0;
    FOR i IN 1..11 LOOP
        s := s + substr(digits, i, 1)::int * w2[i];
    END LOOP;
    d12 := s % 11 % 10;

    RETURN digits || d12::text;
END
$$;

CREATE FUNCTION demo_util.snils(base bigint) RETURNS varchar(14)
    LANGUAGE plpgsql IMMUTABLE AS
$$
DECLARE
    digits text := lpad((100000000 + (base * 715827883 + 87654321) % 900000000)::text, 9, '0');
    s int := 0;
    control int;
BEGIN
    FOR i IN 1..9 LOOP
        s := s + substr(digits, i, 1)::int * (10 - i);
    END LOOP;

    control := CASE
        WHEN s < 100 THEN s
        WHEN s IN (100, 101) THEN 0
        WHEN s % 101 = 100 THEN 0
        ELSE s % 101
    END;

    RETURN substr(digits, 1, 3) || '-' || substr(digits, 4, 3) || '-' ||
           substr(digits, 7, 3) || ' ' || lpad(control::text, 2, '0');
END
$$;

INSERT INTO clients (email, full_name, phone, inn, snils, client_code,
                     birth_date, city, address, profile, marital_status,
                     segment, created_at)
SELECT
    lower(demo_util.pick(ARRAY['sidorov','petrov','kuznecov','morozova','volkov',
                               'zaharova','lebedev','sokolova','popov','novikova'], i))
        || '.' || i || '@' ||
        demo_util.pick(ARRAY['mail.ru','yandex.ru','gmail.com','rambler.ru'], i * 3),

    -- Род согласован намеренно. Источник, который сам себе противоречит,
    -- обесценил бы проверку правдоподобности замен: сравнивать было бы не с чем.
    CASE WHEN i % 2 = 0 THEN
        demo_util.pick(ARRAY['Сидоров','Петров','Кузнецов','Волков',
                             'Лебедев','Попов'], i) || ' ' ||
        demo_util.pick(ARRAY['Иван','Пётр','Алексей','Дмитрий',
                             'Сергей','Николай'], i * 7) || ' ' ||
        demo_util.pick(ARRAY['Иванович','Петрович','Алексеевич',
                             'Дмитриевич','Олегович','Павлович'], i * 11)
    ELSE
        demo_util.pick(ARRAY['Сидорова','Петрова','Кузнецова','Морозова',
                             'Захарова','Соколова','Новикова'], i) || ' ' ||
        demo_util.pick(ARRAY['Мария','Ольга','Анна','Елена',
                             'Дарья','Ирина'], i * 7) || ' ' ||
        demo_util.pick(ARRAY['Ивановна','Петровна','Сергеевна',
                             'Андреевна','Николаевна','Ильинична'], i * 11)
    END,

    '+79' || lpad((100000000 + (i::bigint * 512927377 + 4919) % 900000000)::text, 9, '0'),
    demo_util.inn12(i),
    demo_util.snils(i),
    (1000 + (i * 97 + 40) % 9000)::text || ' ' ||
        lpad((100000 + (i * 7717 + 3) % 900000)::text, 6, '0'),
    DATE '1955-01-01' + ((i * 137) % 16000),
    demo_util.pick(ARRAY['Москва','Санкт-Петербург','Казань','Новосибирск',
                         'Екатеринбург','Нижний Новгород'], i * 5),
    'ул. ' || demo_util.pick(ARRAY['Лесная','Садовая','Заречная','Полевая',
                                   'Центральная','Луговая'], i * 13)
        || ', д. ' || (1 + i % 90) || ', кв. ' || (1 + i % 250),

    jsonb_build_object(
        'contact_person',
        demo_util.pick(ARRAY['Егоров Артём Русланович','Белова Дарья Олеговна',
                             'Титов Роман Львович','Крылова Инна Марковна'], i * 17),
        'contact_phone', '+79' || lpad((100000000 + (i::bigint * 393342739 + 71) % 900000000)::text, 9, '0'),
        'loyalty_points', (i * 13) % 5000
    ),

    demo_util.pick(ARRAY['single','married','divorced','widowed'], i * 23),
    demo_util.pick(ARRAY['retail','smb','enterprise','vip'], i * 19),
    TIMESTAMPTZ '2024-01-01 00:00:00+03' + (i || ' minutes')::interval
FROM generate_series(1, 2000) AS i;

INSERT INTO managers (full_name, email, department)
SELECT
    demo_util.pick(ARRAY['Ерофеев Глеб Тимурович','Панкратова Вера Ильинична',
                         'Тихонов Марк Данилович','Ковалёва Зоя Аркадьевна',
                         'Гущин Артур Валентинович'], i),
    'manager.' || i || '@outtech.example',
    demo_util.pick(ARRAY['Продажи','Поддержка','Логистика','Финансы'], i * 3)
FROM generate_series(1, 40) AS i;

INSERT INTO orders (client_email, manager_id, amount, status, comment, created_at)
SELECT
    c.email,
    1 + (i % 40),
    ((i * 977) % 900000)::numeric / 100 + 100,
    demo_util.pick(ARRAY['new','paid','shipped','closed','cancelled'], i * 7),
    CASE WHEN i % 4 = 0 THEN
        'Клиент ' || c.full_name || ' просил перезвонить на ' || c.phone ||
        '. Доставка по адресу ' || c.address || '.'
    ELSE
        'Стандартная отгрузка со склада, комментариев нет.'
    END,
    TIMESTAMPTZ '2024-06-01 00:00:00+03' + (i || ' minutes')::interval
FROM generate_series(1, 6000) AS i
JOIN clients c ON c.id = 1 + (i * 3) % 2000;

INSERT INTO order_items (order_id, line_no, sku, qty)
SELECT
    o.id,
    n,
    'SKU-' || lpad((((o.id * 31 + n * 7) % 900) + 100)::text, 4, '0'),
    1 + (o.id + n) % 9
FROM orders o
CROSS JOIN generate_series(1, 3) AS n
WHERE (o.id + n) % 4 <> 0;

INSERT INTO support_tickets (client_email, subject, body, created_at)
SELECT
    c.email,
    demo_util.pick(ARRAY['Не пришёл заказ','Вопрос по оплате',
                         'Смена контактных данных','Возврат товара'], i),
    'Обращение от ' || c.full_name || ' (' || c.email || '). ' ||
    CASE WHEN i % 3 = 0 THEN
        'Просит изменить паспортные данные, в анкете указано ' || c.client_code ||
        ', ИНН ' || c.inn || '. '
    ELSE '' END ||
    'Тикет обработан оператором, статус закрыт.',
    TIMESTAMPTZ '2024-09-01 00:00:00+03' + (i || ' minutes')::interval
FROM generate_series(1, 3000) AS i
JOIN clients c ON c.id = 1 + (i * 7) % 2000;

DROP SCHEMA demo_util CASCADE;

ANALYZE;
