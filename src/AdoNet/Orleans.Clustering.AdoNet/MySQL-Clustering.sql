-- For each deployment, there will be only one (active) membership version table version column which will be updated periodically.
CREATE TABLE OrleansMembershipVersionTable
(
    DeploymentId NVARCHAR(150) NOT NULL,
    Timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    Version INT NOT NULL DEFAULT 0,

    CONSTRAINT PK_OrleansMembershipVersionTable_DeploymentId PRIMARY KEY(DeploymentId)
);

-- Every silo instance has a row in the membership table.
CREATE TABLE OrleansMembershipTable
(
    DeploymentId NVARCHAR(150) NOT NULL,
    Address VARCHAR(45) NOT NULL,
    Port INT NOT NULL,
    Generation INT NOT NULL,
    SiloName NVARCHAR(150) NOT NULL,
    HostName NVARCHAR(150) NOT NULL,
    Status INT NOT NULL,
    ProxyPort INT NULL,
    SuspectTimes VARCHAR(8000) NULL,
    StartTime DATETIME NOT NULL,
    IAmAliveTime DATETIME NOT NULL,

    CONSTRAINT PK_MembershipTable_DeploymentId PRIMARY KEY(DeploymentId, Address, Port, Generation),
    CONSTRAINT FK_MembershipTable_MembershipVersionTable_DeploymentId FOREIGN KEY (DeploymentId) REFERENCES OrleansMembershipVersionTable (DeploymentId)
);

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'UpdateIAmAlivetimeKey','
    -- This is expected to never fail by Orleans, so return value
    -- is not needed nor is it checked.
    UPDATE OrleansMembershipTable
    SET
        IAmAliveTime = GREATEST(IAmAliveTime, @IAmAliveTime)
    WHERE
        DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
        AND Address = @Address AND @Address IS NOT NULL
        AND Port = @Port AND @Port IS NOT NULL
        AND Generation = @Generation AND @Generation IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'InsertMembershipVersionKey','
    INSERT INTO OrleansMembershipVersionTable
    (
        DeploymentId
    )
    SELECT * FROM ( SELECT @DeploymentId ) AS TMP
    WHERE NOT EXISTS
    (
    SELECT 1
    FROM
        OrleansMembershipVersionTable
    WHERE
        DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
    );

    SELECT ROW_COUNT();
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'InsertMembershipKey','
    call InsertMembershipKey(@DeploymentId, @Address, @Port, @Generation,
    @Version, @SiloName, @HostName, @Status, @ProxyPort, @StartTime, @IAmAliveTime);'
);

DELIMITER $$

CREATE PROCEDURE InsertMembershipKey(
    in    _DeploymentId NVARCHAR(150),
    in    _Address VARCHAR(45),
    in    _Port INT,
    in    _Generation INT,
    in    _Version INT,
    in    _SiloName NVARCHAR(150),
    in    _HostName NVARCHAR(150),
    in    _Status INT,
    in    _ProxyPort INT,
    in    _StartTime DATETIME,
    in    _IAmAliveTime DATETIME
)
BEGIN
    DECLARE _ROWCOUNT INT;
    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        RESIGNAL;
    END;
    START TRANSACTION;

    UPDATE OrleansMembershipVersionTable
    SET Version = Version + 1
    WHERE DeploymentId = _DeploymentId AND _DeploymentId IS NOT NULL
        AND Version = _Version AND _Version IS NOT NULL AND Version < 2147483647;
    SET _ROWCOUNT = ROW_COUNT();

    INSERT INTO OrleansMembershipTable
    (
        DeploymentId,
        Address,
        Port,
        Generation,
        SiloName,
        HostName,
        Status,
        ProxyPort,
        StartTime,
        IAmAliveTime
    )
    SELECT * FROM ( SELECT
        _DeploymentId,
        _Address,
        _Port,
        _Generation,
        _SiloName,
        _HostName,
        _Status,
        _ProxyPort,
        _StartTime,
        _IAmAliveTime) AS TMP
    WHERE _ROWCOUNT > 0 AND NOT EXISTS
    (
    SELECT 1
    FROM
        OrleansMembershipTable
    WHERE
        DeploymentId = _DeploymentId AND _DeploymentId IS NOT NULL
        AND Address = _Address AND _Address IS NOT NULL
        AND Port = _Port AND _Port IS NOT NULL
        AND Generation = _Generation AND _Generation IS NOT NULL
    );

    SET _ROWCOUNT = ROW_COUNT();

    IF _ROWCOUNT = 0
    THEN
        ROLLBACK;
    ELSE
        COMMIT;
    END IF;
    SELECT _ROWCOUNT;
END$$

CREATE PROCEDURE UpdateMembershipKey(
    IN _DeploymentId NVARCHAR(150),
    IN _Address VARCHAR(45),
    IN _Port INT,
    IN _Generation INT,
    IN _Version INT,
    IN _Status INT,
    IN _SuspectTimes VARCHAR(8000),
    IN _IAmAliveTime DATETIME
)
BEGIN
    DECLARE _ROWCOUNT INT;
    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        RESIGNAL;
    END;
    START TRANSACTION;

    UPDATE OrleansMembershipVersionTable
    SET Version = Version + 1
    WHERE DeploymentId = _DeploymentId AND _DeploymentId IS NOT NULL
        AND Version = _Version AND _Version IS NOT NULL AND Version < 2147483647;
    SET _ROWCOUNT = ROW_COUNT();

    UPDATE OrleansMembershipTable
    SET Status = _Status,
        SuspectTimes = _SuspectTimes,
        IAmAliveTime = GREATEST(IAmAliveTime, _IAmAliveTime)
    WHERE DeploymentId = _DeploymentId AND _DeploymentId IS NOT NULL
        AND Address = _Address AND _Address IS NOT NULL
        AND Port = _Port AND _Port IS NOT NULL
        AND Generation = _Generation AND _Generation IS NOT NULL
        AND _ROWCOUNT > 0;
    IF _ROWCOUNT > 0 THEN
        SELECT COUNT(*) INTO _ROWCOUNT FROM OrleansMembershipTable
        WHERE DeploymentId = _DeploymentId AND Address = _Address
            AND Port = _Port AND Generation = _Generation;
    END IF;

    IF _ROWCOUNT = 0 THEN
        ROLLBACK;
    ELSE
        COMMIT;
    END IF;
    SELECT _ROWCOUNT;
END$$

CREATE PROCEDURE CleanupDefunctSiloEntriesKey(
    IN _DeploymentId NVARCHAR(150),
    IN _Address VARCHAR(45),
    IN _Port INT,
    IN _Generation INT,
    IN _Version INT,
    IN _IAmAliveTime DATETIME,
    IN _StartTime DATETIME,
    IN _SuspectTimes VARCHAR(8000))
BEGIN
    DECLARE _ROWCOUNT INT;
    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        RESIGNAL;
    END;
    START TRANSACTION;
    UPDATE OrleansMembershipVersionTable
    SET Version = Version + 1
    WHERE DeploymentId = _DeploymentId AND Version = _Version AND Version < 2147483647;
    SET _ROWCOUNT = ROW_COUNT();

    DELETE FROM OrleansMembershipTable
    WHERE DeploymentId = _DeploymentId AND Status = 6
        AND Address = _Address AND Port = _Port AND Generation = _Generation
        AND IAmAliveTime = _IAmAliveTime AND StartTime = _StartTime
        AND COALESCE(SuspectTimes, '') = COALESCE(_SuspectTimes, '')
        AND _ROWCOUNT > 0;
    SET _ROWCOUNT = ROW_COUNT();
    IF _ROWCOUNT = 0 THEN
        ROLLBACK;
    ELSE
        COMMIT;
    END IF;
    SELECT _ROWCOUNT;
END$$

DELIMITER ;

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'UpdateMembershipKey','
    CALL UpdateMembershipKey(@DeploymentId, @Address, @Port, @Generation,
        @Version, @Status, @SuspectTimes, @IAmAliveTime);
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'GatewaysQueryKey','
    SELECT
        Address,
        ProxyPort,
        Generation
    FROM
        OrleansMembershipTable
    WHERE
        DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL
        AND Status = @Status AND @Status IS NOT NULL
        AND ProxyPort > 0;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'MembershipReadRowKey','
    SELECT
        v.DeploymentId,
        m.Address,
        m.Port,
        m.Generation,
        m.SiloName,
        m.HostName,
        m.Status,
        m.ProxyPort,
        m.SuspectTimes,
        m.StartTime,
        m.IAmAliveTime,
        v.Version
    FROM
        OrleansMembershipVersionTable v
        -- This ensures the version table will returned even if there is no matching membership row.
        LEFT OUTER JOIN OrleansMembershipTable m ON v.DeploymentId = m.DeploymentId
        AND Address = @Address AND @Address IS NOT NULL
        AND Port = @Port AND @Port IS NOT NULL
        AND Generation = @Generation AND @Generation IS NOT NULL
    WHERE
        v.DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'MembershipReadAllKey','
    SELECT
        v.DeploymentId,
        m.Address,
        m.Port,
        m.Generation,
        m.SiloName,
        m.HostName,
        m.Status,
        m.ProxyPort,
        m.SuspectTimes,
        m.StartTime,
        m.IAmAliveTime,
        v.Version
    FROM
        OrleansMembershipVersionTable v LEFT OUTER JOIN OrleansMembershipTable m
        ON v.DeploymentId = m.DeploymentId
    WHERE
        v.DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'DeleteMembershipTableEntriesKey','
    DELETE FROM OrleansMembershipTable
    WHERE DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
    DELETE FROM OrleansMembershipVersionTable
    WHERE DeploymentId = @DeploymentId AND @DeploymentId IS NOT NULL;
');

INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'CleanupDefunctSiloEntriesKey','
    CALL CleanupDefunctSiloEntriesKey(@DeploymentId, @Address, @Port, @Generation,
        @Version, @IAmAliveTime, @StartTime, @SuspectTimes);
');
