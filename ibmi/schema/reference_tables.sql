-- Reference tables: stores, items, series and the calendar.
-- Names are unqualified. Run with
--   RUNSQLSTM SRCSTMF(...) COMMIT(*NONE) NAMING(*SQL) DFTRDBCOL(<library>) ERRLVL(20)
-- PUB400 user libraries have no journal, so each CREATE warns SQL7905 at severity 20.
-- Foreign keys use RESTRICT, which needs no journal.
-- See design.md for the keys and why each table exists.

CREATE TABLE STORE (
    store_id VARCHAR(8) NOT NULL,
    state_id CHAR(2) NOT NULL,
    PRIMARY KEY (store_id)
);

CREATE TABLE DEPT (
    dept_id VARCHAR(20) NOT NULL,
    cat_id VARCHAR(20) NOT NULL,
    PRIMARY KEY (dept_id)
);

CREATE TABLE ITEM (
    item_id VARCHAR(20) NOT NULL,
    dept_id VARCHAR(20) NOT NULL,
    PRIMARY KEY (item_id),
    FOREIGN KEY (dept_id) REFERENCES DEPT (dept_id)
        ON DELETE RESTRICT ON UPDATE RESTRICT
);

-- One row per item at a store. id is M5's series id, the key SALES and FORECAST use.
CREATE TABLE SERIES (
    id VARCHAR(40) NOT NULL,
    item_id VARCHAR(20) NOT NULL,
    store_id VARCHAR(8) NOT NULL,
    PRIMARY KEY (id),
    UNIQUE (item_id, store_id),
    FOREIGN KEY (item_id) REFERENCES ITEM (item_id)
        ON DELETE RESTRICT ON UPDATE RESTRICT,
    FOREIGN KEY (store_id) REFERENCES STORE (store_id)
        ON DELETE RESTRICT ON UPDATE RESTRICT
);

-- d is M5's day label (d_1 is 2011-01-29); wm_yr_wk is the week sell prices are keyed on.
CREATE TABLE CALENDAR (
    date DATE NOT NULL,
    d VARCHAR(6) NOT NULL,
    wm_yr_wk INTEGER NOT NULL,
    PRIMARY KEY (date),
    UNIQUE (d)
);

CREATE TABLE EVENT (
    event_name VARCHAR(40) NOT NULL,
    event_type VARCHAR(20) NOT NULL,
    PRIMARY KEY (event_name)
);

-- A day can carry more than one event.
CREATE TABLE CAL_EVENT (
    date DATE NOT NULL,
    event_name VARCHAR(40) NOT NULL,
    PRIMARY KEY (date, event_name),
    FOREIGN KEY (date) REFERENCES CALENDAR (date)
        ON DELETE RESTRICT ON UPDATE RESTRICT,
    FOREIGN KEY (event_name) REFERENCES EVENT (event_name)
        ON DELETE RESTRICT ON UPDATE RESTRICT
);

-- A row means SNAP benefits are paid in that state on that day.
CREATE TABLE SNAP_DAY (
    state_id CHAR(2) NOT NULL,
    date DATE NOT NULL,
    PRIMARY KEY (state_id, date),
    FOREIGN KEY (date) REFERENCES CALENDAR (date)
        ON DELETE RESTRICT ON UPDATE RESTRICT
);
