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
        // Logging helper
        // =========================================================
        private static void Log(string msg) => Console.WriteLine($"[DBINIT] {msg}");

        // =========================================================
        // A) TENANT table for header/footer branding
        // =========================================================
        public void EnsureTenantSchema()
        {
            using var conn = _db.Open();

            Log("Checking table TENANT ...");
            if (TableExists(conn, "TENANT"))
            {
                Log("Table TENANT already exists, skip.");
                return;
            }

            Log("Creating table TENANT + seq + trigger + unique index ...");

            var statements = new List<string>
            {
                @"
CREATE TABLE TENANT (
    TENANT_ID        BIGINT NOT NULL,
    TENANT_CODE      VARCHAR(50) CHARACTER SET UTF8 NOT NULL,
    TENANT_NAME      VARCHAR(200) CHARACTER SET UTF8 NOT NULL,

    -- Header branding
    HEADER_LOGO_URL  VARCHAR(500) CHARACTER SET UTF8,
    HEADER_TEXT1     VARCHAR(200) CHARACTER SET UTF8,
    HEADER_TEXT2     VARCHAR(200) CHARACTER SET UTF8,

    -- Footer branding
    FOOTER_TEXT1     VARCHAR(200) CHARACTER SET UTF8,
    FOOTER_TEXT2     VARCHAR(200) CHARACTER SET UTF8,
    FOOTER_TEXT3     VARCHAR(200) CHARACTER SET UTF8,
    FOOTER_IMAGE_URL VARCHAR(500) CHARACTER SET UTF8,

    -- Status + audit
    IS_ACTIVE        SMALLINT DEFAULT 1,
    CREATED_DT       TIMESTAMP DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT PK_TENANT PRIMARY KEY (TENANT_ID)
)",
                @"CREATE SEQUENCE SEQ_TENANT",
                @"
CREATE TRIGGER BI_TENANT FOR TENANT
ACTIVE BEFORE INSERT POSITION 0
AS
BEGIN
    IF (NEW.TENANT_ID IS NULL) THEN
        NEW.TENANT_ID = NEXT VALUE FOR SEQ_TENANT;
END",
                @"CREATE UNIQUE INDEX UX_TENANT_CODE ON TENANT (TENANT_CODE)"
            };

            ExecBatch(conn, statements);

            Log("TENANT created OK.");

            Log("Inserting default TENANT row (TENANT_CODE='DEFAULT') ...");
            ExecNonQuery(conn, @"
INSERT INTO TENANT (TENANT_CODE, TENANT_NAME, HEADER_TEXT1, FOOTER_TEXT1, IS_ACTIVE)
VALUES ('DEFAULT', 'Default Company', 'Welcome', 'Thank you', 1)
");
            Log("Default TENANT row inserted OK.");
        }

        // =========================================================
        // 1) Ensure AGENT.EMAIL column exists
        // =========================================================
        public void EnsureAgentEmailColumn()
        {
            using var conn = _db.Open();
            Log("Checking AGENT.EMAIL ...");

            if (ColumnExists(conn, "AGENT", "EMAIL"))
            {
                Log("AGENT.EMAIL already exists, skip.");
                return;
            }

            Log("Adding column AGENT.EMAIL ...");
            ExecNonQuery(conn, "ALTER TABLE AGENT ADD EMAIL VARCHAR(120) CHARACTER SET UTF8");
            ExecNonQuery(conn, "UPDATE AGENT SET EMAIL = '' WHERE EMAIL IS NULL");
            Log("AGENT.EMAIL added OK.");
        }

        // =========================================================
        // 1b) Ensure AGENT.BRANCHNO column exists
        // =========================================================
        public void EnsureAgentBranchNoColumn()
        {
            using var conn = _db.Open();
            Log("Checking AGENT.BRANCHNO ...");

            if (ColumnExists(conn, "AGENT", "BRANCHNO"))
            {
                Log("AGENT.BRANCHNO already exists, skip.");
                return;
            }

            Log("Adding column AGENT.BRANCHNO ...");
            ExecNonQuery(conn, "ALTER TABLE AGENT ADD BRANCHNO VARCHAR(10) CHARACTER SET UTF8");
            ExecNonQuery(conn, "UPDATE AGENT SET BRANCHNO = '' WHERE BRANCHNO IS NULL");
            Log("AGENT.BRANCHNO added OK.");
        }

        // =========================================================
        // 1c) Ensure BRANCH table exists + FK from AGENT(BRANCHNO)
        // =========================================================
        public void EnsureBranchSchema()
        {
            using var conn = _db.Open();

            Log("Checking table BRANCH ...");
            if (!TableExists(conn, "BRANCH"))
            {
                Log("Creating table BRANCH ...");
                ExecNonQuery(conn, @"
CREATE TABLE BRANCH (
    BRANCHNO    VARCHAR(10) CHARACTER SET UTF8 NOT NULL,
    BRANCHNAME  VARCHAR(120) CHARACTER SET UTF8,
    CONSTRAINT PK_BRANCH PRIMARY KEY (BRANCHNO)
)");
                Log("Table BRANCH created OK.");
            }
            else
            {
                Log("Table BRANCH already exists, skip.");
            }

            Log("Checking column AGENT.BRANCHNO ...");
            if (!ColumnExists(conn, "AGENT", "BRANCHNO"))
            {
                Log("Adding column AGENT.BRANCHNO ...");
                ExecNonQuery(conn, "ALTER TABLE AGENT ADD BRANCHNO VARCHAR(10) CHARACTER SET UTF8");
                ExecNonQuery(conn, "UPDATE AGENT SET BRANCHNO = '' WHERE BRANCHNO IS NULL");
                Log("AGENT.BRANCHNO added OK.");
            }
            else
            {
                Log("AGENT.BRANCHNO already exists, skip.");
            }

            Log("Checking FK FK_AGENT_BRANCH ...");
            EnsureForeignKey(
                conn,
                fkName: "FK_AGENT_BRANCH",
                alterSql: "ALTER TABLE AGENT ADD CONSTRAINT FK_AGENT_BRANCH FOREIGN KEY (BRANCHNO) REFERENCES BRANCH(BRANCHNO)"
            );
        }

        // =========================================================
        // ✅ Ensure SL_SODTL has UDF_CLAIMED + UDF_PREV_CLAIMED (default 0)
        // =========================================================
        public void EnsureSalesOrderDetailClaimColumns()
        {
            using var conn = _db.Open();

            Log("Checking SL_SODTL.UDF_CLAIMED + SL_SODTL.UDF_PREV_CLAIMED ...");

            EnsureDecimalColumnWithDefaultZero(conn, tableName: "SL_SODTL", columnName: "UDF_CLAIMED");
            EnsureDecimalColumnWithDefaultZero(conn, tableName: "SL_SODTL", columnName: "UDF_PREV_CLAIMED");

            Log("SL_SODTL claim columns ensured OK.");
        }

        // Adds DECIMAL(18,2) DEFAULT 0 and backfills NULLs to 0
        private void EnsureDecimalColumnWithDefaultZero(FbConnection conn, string tableName, string columnName)
        {
            var t = tableName.ToUpperInvariant();
            var c = columnName.ToUpperInvariant();

            if (ColumnExists(conn, t, c))
            {
                Log($"{t}.{c} already exists, skip.");
                return;
            }

            Log($"Adding column {t}.{c} (DECIMAL(18,2) DEFAULT 0) ...");
            ExecNonQuery(conn, $"ALTER TABLE {t} ADD {c} DECIMAL(18,2) DEFAULT 0");
            ExecNonQuery(conn, $"UPDATE {t} SET {c} = 0 WHERE {c} IS NULL");
            Log($"{t}.{c} added + backfilled OK.");
        }

        // =========================================================
        // 2) Ensure APPOINTMENT schema exists
        // =========================================================
        public void EnsureAppointmentSchema()
        {
            using var conn = _db.Open();

            Log("Checking table APPOINTMENT ...");
            if (TableExists(conn, "APPOINTMENT"))
            {
                Log("Table APPOINTMENT already exists, will ensure APPT_DTL ...");
                EnsureApptDtlSchema(conn);
                return;
            }

            Log("Creating table APPOINTMENT + seq + trigger + indexes + constraints ...");

            var statements = new List<string>
            {
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
                @"CREATE SEQUENCE SEQ_APPOINTMENT",
                @"
CREATE TRIGGER BI_APPOINTMENT FOR APPOINTMENT
ACTIVE BEFORE INSERT POSITION 0
AS
BEGIN
    IF (NEW.APPT_ID IS NULL) THEN
        NEW.APPT_ID = NEXT VALUE FOR SEQ_APPOINTMENT;
END",
                @"CREATE INDEX IX_APPT_START       ON APPOINTMENT (APPT_START)",
                @"CREATE INDEX IX_APPT_AGENT_START ON APPOINTMENT (AGENT_CODE, APPT_START)",
                @"CREATE INDEX IX_APPT_CUST_START  ON APPOINTMENT (CUSTOMER_CODE, APPT_START)",

                @"ALTER TABLE APPOINTMENT ADD CONSTRAINT CK_APPT_TIME CHECK (APPT_END IS NULL OR APPT_END >= APPT_START)",

                @"ALTER TABLE APPOINTMENT ADD CONSTRAINT FK_APPT_CUST  FOREIGN KEY (CUSTOMER_CODE) REFERENCES AR_CUSTOMER (CODE)",
                @"ALTER TABLE APPOINTMENT ADD CONSTRAINT FK_APPT_AGENT FOREIGN KEY (AGENT_CODE)    REFERENCES AGENT (CODE)"
            };

            ExecBatch(conn, statements);

            Log("APPOINTMENT created OK. Now ensuring APPT_DTL ...");
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

        private void EnsureApptDtlSchema(FbConnection conn)
        {
            Log("Checking table APPT_DTL ...");
            if (TableExists(conn, "APPT_DTL"))
            {
                Log("Table APPT_DTL already exists, skip.");
                return;
            }

            Log("Creating table APPT_DTL + seq + trigger ...");

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

            ExecBatch(conn, statements);

            Log("APPT_DTL created OK.");
        }

        // =========================================================
        // 4) Ensure APPT_SIGNATURE schema exists (Option B: 1 row per APPT)
        // =========================================================
        public void EnsureApptSignatureSchema()
        {
            using var conn = _db.Open();

            Log("Checking table APPT_SIGNATURE ...");
            if (TableExists(conn, "APPT_SIGNATURE"))
            {
                Log("Table APPT_SIGNATURE already exists, skip.");
                return;
            }

            Log("Creating table APPT_SIGNATURE + seq + trigger + FK + unique index ...");

            var statements = new List<string>
            {
                @"
CREATE TABLE APPT_SIGNATURE (
    SIGN_ID        BIGINT NOT NULL,
    APPT_ID        BIGINT NOT NULL,

    SIGNED_DT      TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    SIGNED_BY      VARCHAR(120) CHARACTER SET UTF8,
    REMARKS        VARCHAR(300) CHARACTER SET UTF8,

    SIGNATURE_PNG  BLOB SUB_TYPE 0,

    CONSTRAINT PK_APPT_SIGNATURE PRIMARY KEY (SIGN_ID),
    CONSTRAINT FK_APPT_SIGNATURE_APPT FOREIGN KEY (APPT_ID) REFERENCES APPOINTMENT (APPT_ID)
)",
                @"CREATE SEQUENCE SEQ_APPT_SIGNATURE",
                @"
CREATE TRIGGER BI_APPT_SIGNATURE FOR APPT_SIGNATURE
ACTIVE BEFORE INSERT POSITION 0
AS
BEGIN
    IF (NEW.SIGN_ID IS NULL) THEN
        NEW.SIGN_ID = NEXT VALUE FOR SEQ_APPT_SIGNATURE;
END",
                @"CREATE UNIQUE INDEX UX_APPT_SIGNATURE_APPT ON APPT_SIGNATURE (APPT_ID)",
                @"CREATE INDEX IX_APPT_SIGNATURE_APPT_DT ON APPT_SIGNATURE (APPT_ID, SIGNED_DT)"
            };

            ExecBatch(conn, statements);

            Log("APPT_SIGNATURE created OK.");
        }

        // =========================================================
        // ✅ Ensure APPOINTMENT_LOG table exists
        // =========================================================
        public void EnsureAppointmentLogTable()
        {
            using var conn = _db.Open();

            Log("Checking table APPOINTMENT_LOG ...");
            if (TableExists(conn, "APPOINTMENT_LOG"))
            {
                Log("Table APPOINTMENT_LOG already exists, skip.");
                return;
            }

            Log("Creating table APPOINTMENT_LOG ...");
            ExecNonQuery(conn, @"
CREATE TABLE APPOINTMENT_LOG (
    LOG_ID BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    APPT_ID BIGINT NOT NULL,
    ACTION_TYPE VARCHAR(32) NOT NULL,
    ACTION_TIME TIMESTAMP NOT NULL,
    USERNAME VARCHAR(64),
    DETAILS VARCHAR(1024),
    SO_QTY INTEGER,
    CLAIMED INTEGER,
    PREV_CLAIMED INTEGER,
    CURR_CLAIMED INTEGER,
    SERVICE_CODE VARCHAR(160)
)");
            Log("APPOINTMENT_LOG table created OK.");
        }

        // =========================================================
        // Helpers (exists + execute)
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
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT COUNT(*)
FROM rdb$relation_constraints
WHERE rdb$constraint_type = 'FOREIGN KEY'
  AND rdb$constraint_name = @FK";
            cmd.Parameters.Add(new FbParameter("@FK", fkName.ToUpperInvariant()));

            var exists = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            if (exists)
            {
                Log($"FK {fkName} already exists, skip.");
                return;
            }

            Log($"Adding FK {fkName} ...");
            ExecNonQuery(conn, alterSql);
            Log($"FK {fkName} added OK.");
        }

        private static void ExecBatch(FbConnection conn, IEnumerable<string> statements)
        {
            foreach (var sql in statements)
                ExecNonQuery(conn, sql);
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
