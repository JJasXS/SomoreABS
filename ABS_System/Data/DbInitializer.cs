using System;
using System.Collections.Generic;
using FirebirdSql.Data.FirebirdClient;

namespace YourApp.Data
{
    /// <summary>
    /// DbInitializer ensures required schema upgrades exist at app startup.
    /// Call these methods once when your app boots (Program.cs / Startup).
    /// </summary>
    public class DbInitializer
    {
        private readonly FirebirdDb _db;

        public DbInitializer(FirebirdDb db)
        {
            _db = db;
        }

        // =========================================================
        // 1) Ensure AGENT.EMAIL column exists
        // =========================================================
        public void EnsureAgentEmailColumn()
        {
            using var conn = _db.Open();

            if (ColumnExists(conn, "AGENT", "EMAIL"))
                return;

            ExecNonQuery(conn, "ALTER TABLE AGENT ADD EMAIL VARCHAR(120) CHARACTER SET UTF8");
            ExecNonQuery(conn, "UPDATE AGENT SET EMAIL = '' WHERE EMAIL IS NULL");
        }

        // =========================================================
        // 1b) Ensure AGENT.BRANCHNO column exists
        // =========================================================
        public void EnsureAgentBranchNoColumn()
        {
            using var conn = _db.Open();

            if (ColumnExists(conn, "AGENT", "BRANCHNO"))
                return;

            ExecNonQuery(conn, "ALTER TABLE AGENT ADD BRANCHNO VARCHAR(10) CHARACTER SET UTF8");
            ExecNonQuery(conn, "UPDATE AGENT SET BRANCHNO = '' WHERE BRANCHNO IS NULL");
        }

        // =========================================================
        // 1c) Ensure BRANCH table exists + FK from AGENT(BRANCHNO)
        // =========================================================
        public void EnsureBranchSchema()
        {
            using var conn = _db.Open();

            // Create BRANCH table if not exists
            if (!TableExists(conn, "BRANCH"))
            {
                ExecNonQuery(conn, @"
CREATE TABLE BRANCH (
    BRANCHNO    VARCHAR(10) CHARACTER SET UTF8 NOT NULL,
    BRANCHNAME  VARCHAR(120) CHARACTER SET UTF8,
    CONSTRAINT PK_BRANCH PRIMARY KEY (BRANCHNO)
)");
            }

            // Ensure BRANCHNO column exists in AGENT
            if (!ColumnExists(conn, "AGENT", "BRANCHNO"))
            {
                ExecNonQuery(conn, "ALTER TABLE AGENT ADD BRANCHNO VARCHAR(10) CHARACTER SET UTF8");
                ExecNonQuery(conn, "UPDATE AGENT SET BRANCHNO = '' WHERE BRANCHNO IS NULL");
            }

            // Add FK constraint if not exists
            EnsureForeignKey(
                conn,
                fkName: "FK_AGENT_BRANCH",
                alterSql: "ALTER TABLE AGENT ADD CONSTRAINT FK_AGENT_BRANCH FOREIGN KEY (BRANCHNO) REFERENCES BRANCH(BRANCHNO)"
            );
        }

        // =========================================================
        // 2) Ensure APPOINTMENT schema exists
        // =========================================================
        public void EnsureAppointmentSchema()
        {
            using var conn = _db.Open();

            // If table already exists, still ensure APPT_DTL exists (safe upgrade)
            if (TableExists(conn, "APPOINTMENT"))
            {
                EnsureApptDtlSchema(conn);
                return;
            }

            var statements = new List<string>
            {
                // 1) Create table
                @"
CREATE TABLE APPOINTMENT (
    APPT_ID        BIGINT NOT NULL,

    CUSTOMER_CODE  VARCHAR(10) CHARACTER SET UTF8 NOT NULL,
    AGENT_CODE     VARCHAR(10) CHARACTER SET UTF8 NOT NULL,

    APPT_START     TIMESTAMP NOT NULL,
    APPT_END       TIMESTAMP,

    TITLE          VARCHAR(200) CHARACTER SET UTF8,
    NOTES          BLOB SUB_TYPE TEXT CHARACTER SET UTF8,

    STATUS         VARCHAR(20) CHARACTER SET UTF8 DEFAULT 'NEW',
    CREATED_DT     TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    CREATED_BY     VARCHAR(80) CHARACTER SET UTF8,

    LAST_UPD_DT    TIMESTAMP,
    LAST_UPD_BY    VARCHAR(80) CHARACTER SET UTF8,

    CONSTRAINT PK_APPOINTMENT PRIMARY KEY (APPT_ID)
)",

                // 2) Sequence
                @"CREATE SEQUENCE SEQ_APPOINTMENT",

                // 3) Trigger (auto ID)
                @"
CREATE TRIGGER BI_APPOINTMENT FOR APPOINTMENT
ACTIVE BEFORE INSERT POSITION 0
AS
BEGIN
    IF (NEW.APPT_ID IS NULL) THEN
        NEW.APPT_ID = NEXT VALUE FOR SEQ_APPOINTMENT;
END",

                // 4) Indexes
                @"CREATE INDEX IX_APPT_START        ON APPOINTMENT (APPT_START)",
                @"CREATE INDEX IX_APPT_AGENT_START  ON APPOINTMENT (AGENT_CODE, APPT_START)",
                @"CREATE INDEX IX_APPT_CUST_START   ON APPOINTMENT (CUSTOMER_CODE, APPT_START)",

                // 5) Check constraint
                @"ALTER TABLE APPOINTMENT ADD CONSTRAINT CK_APPT_TIME CHECK (APPT_END IS NULL OR APPT_END >= APPT_START)",

                // 6) Foreign keys (requires AR_CUSTOMER.CODE and AGENT.CODE to be PK/UNIQUE)
                @"ALTER TABLE APPOINTMENT ADD CONSTRAINT FK_APPT_CUST  FOREIGN KEY (CUSTOMER_CODE) REFERENCES AR_CUSTOMER (CODE)",
                @"ALTER TABLE APPOINTMENT ADD CONSTRAINT FK_APPT_AGENT FOREIGN KEY (AGENT_CODE)    REFERENCES AGENT (CODE)"
            };

            foreach (var sql in statements)
                ExecNonQuery(conn, sql);

            // Create detail table after appointment
            EnsureApptDtlSchema(conn);
        }

        // =========================================================
        // 3) Ensure APPT_DTL schema exists (multiple services per appointment)
        // =========================================================
        public void EnsureApptDtlSchema()
        {
            using var conn = _db.Open();
            EnsureApptDtlSchema(conn);
        }

        // internal version so EnsureAppointmentSchema can reuse same connection
        private void EnsureApptDtlSchema(FbConnection conn)
        {
            if (TableExists(conn, "APPT_DTL"))
                return;

            var statements = new List<string>
            {
                @"
CREATE TABLE APPT_DTL (
    APPT_DTL_ID   BIGINT NOT NULL,
    APPT_ID       BIGINT NOT NULL,
    SERVICE_CODE  VARCHAR(40) CHARACTER SET UTF8,
    SERVICE_DESC  VARCHAR(200) CHARACTER SET UTF8,
    QTY           INT DEFAULT 1,
    PRICE         DECIMAL(18,2),
    CONSTRAINT PK_APPT_DTL PRIMARY KEY (APPT_DTL_ID),
    CONSTRAINT FK_APPT_DTL_APPT FOREIGN KEY (APPT_ID) REFERENCES APPOINTMENT (APPT_ID)
)",

                @"CREATE SEQUENCE SEQ_APPT_DTL",

                @"
CREATE TRIGGER BI_APPT_DTL FOR APPT_DTL
ACTIVE BEFORE INSERT POSITION 0
AS
BEGIN
    IF (NEW.APPT_DTL_ID IS NULL) THEN
        NEW.APPT_DTL_ID = NEXT VALUE FOR SEQ_APPT_DTL;
END"
            };

            foreach (var sql in statements)
                ExecNonQuery(conn, sql);
        }

        // =========================================================
        // Helpers
        // =========================================================
        private static bool TableExists(FbConnection conn, string tableName)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT COUNT(*)
FROM rdb$relations
WHERE rdb$system_flag = 0
  AND rdb$relation_name = @TNAME";
            cmd.Parameters.Add(new FbParameter("@TNAME", tableName.ToUpperInvariant()));

            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        private static bool ColumnExists(FbConnection conn, string tableName, string columnName)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT COUNT(*)
FROM rdb$relation_fields
WHERE rdb$relation_name = @TNAME
  AND rdb$field_name    = @CNAME";
            cmd.Parameters.Add(new FbParameter("@TNAME", tableName.ToUpperInvariant()));
            cmd.Parameters.Add(new FbParameter("@CNAME", columnName.ToUpperInvariant()));

            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }

        private static void EnsureForeignKey(FbConnection conn, string fkName, string alterSql)
        {
            // Firebird stores constraints in RDB$RELATION_CONSTRAINTS
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT COUNT(*)
FROM rdb$relation_constraints
WHERE rdb$constraint_type = 'FOREIGN KEY'
  AND rdb$constraint_name = @FK";
            cmd.Parameters.Add(new FbParameter("@FK", fkName.ToUpperInvariant()));

            var exists = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            if (exists) return;

            ExecNonQuery(conn, alterSql);
        }

        private static void ExecNonQuery(FbConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            try
            {
                cmd.ExecuteNonQuery();
            }
            catch (FbException ex)
            {
                throw new Exception(
                    $"Schema init failed on:\n{sql}\n\nFirebird error: {ex.Message}",
                    ex
                );
            }
        }
    }
}
