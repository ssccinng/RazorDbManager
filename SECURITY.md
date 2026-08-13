# Security policy

RazorDbManager is a privileged database administration surface. Do not expose
it without host authentication, authorization policies, HTTPS, least-privilege
database credentials, and durable audit storage.

Please report vulnerabilities privately to the repository maintainers. Do not
include credentials, connection strings, production data, or exploit payloads
from a live system in a public issue.

Supported releases receive security fixes on the latest minor version. The
project does not treat SQL parsing or UI visibility as an authorization
boundary; database grants and server-side policy checks are always required.

Configured capabilities are an application-side ceiling, not a prediction of
the server account's effective grants. The live status probe conservatively
parses `SHOW GRANTS` for diagnostics only; it doesn't understand every role or
server-specific grant form and never authorizes an operation. Use dedicated
least-privilege credentials and rely on the database server as the final
authorization boundary.

SQL restore executes scripts as arbitrary SQL. Its lexical schema checks reject
obvious out-of-scope references for early feedback, but they cannot constrain
dynamic SQL, stored programs, triggers, or every server-specific construct.
Always give `SqlConsoleConnectionStringName` a dedicated account whose database
grants are the real restore boundary; never reuse an administrative account.

The default artifact and SQLite stores are single-instance local files. Protect
the configured `StoragePath` with operating-system ACLs so only the application
identity and administrators can read or modify descriptors, audit records,
one-time tokens, or transfer payloads. Replace these stores for multi-instance
deployment or when storage is shared with another process identity.
