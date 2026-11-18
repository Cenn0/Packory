# Native database connector qualification fixtures

`scripts/qualify-database-connectors.sh` provisions one disposable Linux/amd64
database, a temporary CA and server certificate, and a user with SELECT access to
one table. The CI matrix runs PostgreSQL, MySQL, MariaDB, SQL Server and Oracle as
independent required jobs. Missing providers, failed tests, missing results and
skipped provider tests fail qualification.

Each engine must pass all six native provider tests, a separate untrusted-CA
rejection test, and a final positive query after CA trust is restored. The latter
two phases use fresh processes to avoid connection-pool and TLS-session reuse.

The script requires Docker, OpenSSL, Python 3, the .NET 10 SDK, and passwordless
`sudo` for temporary CA trust installation and file ownership. Use an isolated
development machine or CI runner. It removes only its named container, network,
temporary files and trust certificate on exit. It never touches an existing
database. Build the test project before running it:

```bash
dotnet build Packory.slnx -c Release
bash scripts/qualify-database-connectors.sh postgresql
```

All databases use the same synthetic table:

| id | name | amount |
| --- | --- | --- |
| 1 | alpha | 12.50 |
| 2 | beta | 7.25 |
| 3 | gamma | 0.00 |

The table is `packory_connector_fixture`. In Oracle, a separate non-login owner
owns the table; a synonym lets the reader resolve its unqualified name. Giving
the reader ownership would invalidate the write-denial test. No test process
receives administrative database credentials. The SQL Server edition is
Developer, for this non-production fixture only, with its container EULA
accepted explicitly. Oracle Database Free runs in a **community-maintained**
gvenzl image; passing this test is not an Oracle product endorsement or support
certification.

The certificate has only `DNS:db.example.invalid` in its SAN. Packory connects to
the numeric loopback address and verifies the original DNS identity (Oracle:
the configured exact certificate DN). This exercises the separation between
address pinning and TLS identity rather than accepting a certificate for the IP.
No `TrustServerCertificate` or certificate-validation bypass is enabled. Database
bootstrap uses local sockets or the same trusted TLS identity.

`images.json` records immutable manifest digests retrieved from their publisher
registries on the stated date. Floating tag labels are descriptive; the digest
selects the actual content. Update the digest deliberately and repeat all
qualification checks. The Oracle full image retains the `orapki` wallet utility;
the slim and regular images remove Java components used by that utility.

The test environment contract is `PACKORY_TEST_DB_PROVIDER`, `..._HOST`,
`..._ADDRESS`, `..._PORT`, `..._DATABASE`, `..._USER`, `..._PASSWORD`,
`..._TLS_SERVER_NAME`, `..._EXPECTED_DN` and `..._CA_FILE`. Credentials, private
keys, database logs and full TRX files are removed, not uploaded. The evidence
JSON contains only image/source identity and aggregate pass/fail counts.
Set `PACKORY_DB_QUALIFICATION_OUTPUT` to choose its output path; by default the
sanitized evidence remains in the runner temporary directory after cleanup.
The workflow checks out the immutable source SHA recorded by the report.

## Primary references

- [Docker PostgreSQL image source](https://github.com/docker-library/postgres)
- [Docker MySQL image source](https://github.com/docker-library/mysql)
- [MariaDB image source](https://github.com/MariaDB/mariadb-docker)
- [SQL Server Linux TLS configuration](https://learn.microsoft.com/sql/linux/sql-server-linux-configure-mssql-conf#specify-tls-settings)
- [SQL Server container deployment and editions](https://learn.microsoft.com/sql/linux/quickstart-install-connect-docker)
- [Oracle Free community image and flavors](https://github.com/gvenzl/oci-oracle-free)
- [Oracle wallet import commands](https://docs.oracle.com/en/database/oracle/oracle-database/26/dbseg/orapki-utility-commands-summary.html)

These fixtures validate one specific database/driver/environment combination.
They do not validate a customer's permissions, network isolation, failover,
backups, high availability or licensing of a production database.
