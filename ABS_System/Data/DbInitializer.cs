using System;
using System.Collections.Generic;
using FirebirdSql.Data.FirebirdClient;

namespace YourApp.Data
{
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

            // Check if EMAIL column exists in AGENT table
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT COUNT(*)
FROM rdb$relation_fields
WHERE rdb$relation_name = 'AGENT'
  AND rdb$field_name    = 'EMAIL'";

            var exists = Convert.ToInt32(cmd.ExecuteScalar()) > 0;

            if (!exists)
            {
                // Add EMAIL column
                ExecNonQuery(conn, "ALTER TABLE AGENT ADD EMAIL VARCHAR(120) CHARACTER SET UTF8");

                // Optional: Set empty string for existing rows if null
                ExecNonQuery(conn, "UPDATE AGENT SET EMAIL = '' WHERE EMAIL IS NULL");
            }
        }

        // =========================================================
        // 2) Ensure APPOINTMENT schema exists
        // =========================================================
        public void EnsureAppointmentSchema()
        {
            using var conn = _db.Open();

            // If table already exists, do nothing
            if (TableExists(conn, "APPOINTMENT"))
                return;

            // IMPORTANT:
            // Ensure CUSTOMER_CODE / AGENT_CODE match AR_CUSTOMER.CODE and AGENT.CODE types/lengths.
            // Your CODE fields are UTF8 and (often) effectively VARCHAR(10) in Firebird.

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
                @"CREATE INDEX IX_APPT_START       ON APPOINTMENT (APPT_START)",
                @"CREATE INDEX IX_APPT_AGENT_START ON APPOINTMENT (AGENT_CODE, APPT_START)",
                @"CREATE INDEX IX_APPT_CUST_START  ON APPOINTMENT (CUSTOMER_CODE, APPT_START)",

                // 5) Check constraint
                @"ALTER TABLE APPOINTMENT ADD CONSTRAINT CK_APPT_TIME CHECK (APPT_END IS NULL OR APPT_END >= APPT_START)",

                // 6) Foreign keys
                // These succeed only if AR_CUSTOMER.CODE and AGENT.CODE are PK or UNIQUE.
                @"ALTER TABLE APPOINTMENT ADD CONSTRAINT FK_APPT_CUST  FOREIGN KEY (CUSTOMER_CODE) REFERENCES AR_CUSTOMER (CODE)",
                @"ALTER TABLE APPOINTMENT ADD CONSTRAINT FK_APPT_AGENT FOREIGN KEY (AGENT_CODE)    REFERENCES AGENT (CODE)"
            };

            foreach (var sql in statements)
            {
                ExecNonQuery(conn, sql);
            }
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
