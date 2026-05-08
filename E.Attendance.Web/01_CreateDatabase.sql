-- Create E_Attendace database and baseline extensions.
-- Run with postgres superuser.

CREATE DATABASE "E_Attendace"
    WITH
    OWNER = postgres
    ENCODING = 'UTF8'
    TEMPLATE = template0;

\connect "E_Attendace";

CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS "pg_trgm";
CREATE EXTENSION IF NOT EXISTS "btree_gin";

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'E_Attendace') THEN
        CREATE USER "E_Attendace" WITH PASSWORD '<set-secure-password>';
    END IF;
END
$$;

GRANT ALL PRIVILEGES ON DATABASE "E_Attendace" TO postgres;
GRANT CONNECT ON DATABASE "E_Attendace" TO "E_Attendace";

CREATE SCHEMA IF NOT EXISTS E_Attendace AUTHORIZATION "E_Attendace";
GRANT USAGE ON SCHEMA E_Attendace TO "E_Attendace";
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA E_Attendace TO "E_Attendace";
ALTER DEFAULT PRIVILEGES IN SCHEMA E_Attendace
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "E_Attendace";

ALTER DATABASE "E_Attendace" SET timezone TO 'Asia/Kathmandu';
ALTER DATABASE "E_Attendace" SET search_path TO E_Attendace;
