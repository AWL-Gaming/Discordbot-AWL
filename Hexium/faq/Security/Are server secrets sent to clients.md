No. Discord webhook URLs, the bot token, and server-only provider credentials are kept in the server config and are not synchronized to clients.

Clients receive synchronized non-secret settings needed for normal operation, while server-side integrations resolve their own credentials locally.
