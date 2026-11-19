-- Empty governance database for the gateway under test; the gateway migrates it at startup (PostgreSQL is the only supported governance provider).
CREATE DATABASE governance;
