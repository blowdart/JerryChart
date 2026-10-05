DROP TABLE IF EXISTS notes;

CREATE TABLE IF NOT EXISTS Actor (
    Did VARCHAR(2048) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    Handle VARCHAR(253) CHARACTER SET ascii COLLATE ascii_bin NULL,
    UpdatedAt DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6))
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS ActorRefresh (
    Did VARCHAR(2048) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    Revision BIGINT NOT NULL DEFAULT 0,
    NextAttemptAt DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
    INDEX IX_ActorRefresh_NextAttemptAt (NextAttemptAt),
    FOREIGN KEY (Did) REFERENCES Actor (Did)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS MonitorSchemaMigration (
    MigrationId VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    AppliedAt DATETIME(6) NOT NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS Hits (
    AtUriHash BINARY(32) NOT NULL PRIMARY KEY,
    AtUri VARCHAR(8192) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    CreatedAt DATETIME(6) NOT NULL,
    AuthorDid VARCHAR(2048) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    ParentAuthorDid VARCHAR(2048) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    ParentAtUriHash BINARY(32) NULL,
    ParentAtUri VARCHAR(8192) CHARACTER SET ascii COLLATE ascii_bin NULL,
    -- ParentUriBackfillStatus: 0 Pending, 1 Resolved, 2 Unavailable, 3 RetryPending.
    ParentUriBackfillStatus TINYINT UNSIGNED NOT NULL DEFAULT 0,
    ParentUriBackfillAttemptCount INT UNSIGNED NOT NULL DEFAULT 0,
    ParentUriBackfillNextAttemptAt DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
    INDEX IX_Hits_AuthorDid (AuthorDid),
    INDEX IX_Hits_ParentAuthorDid (ParentAuthorDid),
    INDEX IX_Hits_ParentAtUriHash (ParentAtUriHash),
    INDEX IX_Hits_ParentUriBackfill (ParentUriBackfillStatus, ParentUriBackfillNextAttemptAt)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS JetstreamReplayProgress (
    MonitorId VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    CheckpointJson JSON NOT NULL,
    UpdatedAt DATETIME(6) NOT NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS StatisticsUpdate (
    Id TINYINT NOT NULL PRIMARY KEY,
    UpdatedAt DATETIME(6) NOT NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS ProcessingActivity (
    Resource VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    RunId CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    Phase VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    StartedAt DATETIME(6) NOT NULL,
    ChangedAt DATETIME(6) NOT NULL,
    HeartbeatAt DATETIME(6) NOT NULL,
    FinishedAt DATETIME(6) NULL
) ENGINE=InnoDB;
