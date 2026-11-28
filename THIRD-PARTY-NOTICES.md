# Third-party database driver notices

Packory's own source remains Apache-2.0. Dependencies retain their respective licenses;
Packory does not relicense vendor components or grant a database server license.

| Component | Pinned version | Publisher license / original source |
| --- | --- | --- |
| Npgsql | 10.0.3 | [PostgreSQL License](https://github.com/npgsql/npgsql/blob/v10.0.3/LICENSE) |
| MySqlConnector | 3.0.0 | [MIT](https://github.com/mysql-net/MySqlConnector/blob/3.0.0/LICENSE) |
| Microsoft.Data.SqlClient | 7.1.2 | [MIT](https://github.com/dotnet/SqlClient/blob/v7.1.2/LICENSE) |
| Oracle.ManagedDataAccess.Core | 23.26.401 | [Oracle Free Distribution, Hosting, and Use Terms](https://www.nuget.org/packages/Oracle.ManagedDataAccess.Core/23.26.401/License) |

The unmodified Oracle license distributed inside the pinned NuGet package is copied to
`ThirdPartyNotices/Oracle.ManagedDataAccess.Core-LICENSE.txt` in application build and
publish output, including the Packory container. Its source copy is
[here](src/Packory/ThirdPartyNotices/Oracle.ManagedDataAccess.Core-LICENSE.txt).
This notice does not replace the dependency graph, its licenses, or their terms.

The disposable SQL Server test uses the Developer edition; the Oracle test uses an
Oracle Database Free community container. Those fixtures do not establish vendor
endorsement, production licensing, or compatibility with every database edition.
The image publishers, exact digests and sources are recorded in the
[fixture manifest](tests/fixtures/database-connectors/images.json).
