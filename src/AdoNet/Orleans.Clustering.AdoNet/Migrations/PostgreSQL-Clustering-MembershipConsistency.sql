-- Run after the existing clustering schema migrations, before deploying the updated membership provider.
-- Stop every silo using these membership queries before migration; restart only with the updated provider.
-- Providers cache OrleansQuery text, and the new cleanup query requires parameters absent in older providers.
BEGIN;

CREATE OR REPLACE FUNCTION update_i_am_alive_time(
    deployment_id OrleansMembershipTable.DeploymentId%TYPE,
    address_arg OrleansMembershipTable.Address%TYPE,
    port_arg OrleansMembershipTable.Port%TYPE,
    generation_arg OrleansMembershipTable.Generation%TYPE,
    i_am_alive_time OrleansMembershipTable.IAmAliveTime%TYPE)
RETURNS void AS
$func$
BEGIN
    UPDATE OrleansMembershipTable AS d
    SET IAmAliveTime = GREATEST(d.IAmAliveTime, i_am_alive_time)
    WHERE d.DeploymentId = deployment_id AND d.Address = address_arg
        AND d.Port = port_arg AND d.Generation = generation_arg;
END
$func$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION insert_membership(
    DeploymentIdArg OrleansMembershipTable.DeploymentId%TYPE,
    AddressArg OrleansMembershipTable.Address%TYPE,
    PortArg OrleansMembershipTable.Port%TYPE,
    GenerationArg OrleansMembershipTable.Generation%TYPE,
    SiloNameArg OrleansMembershipTable.SiloName%TYPE,
    HostNameArg OrleansMembershipTable.HostName%TYPE,
    StatusArg OrleansMembershipTable.Status%TYPE,
    ProxyPortArg OrleansMembershipTable.ProxyPort%TYPE,
    StartTimeArg OrleansMembershipTable.StartTime%TYPE,
    IAmAliveTimeArg OrleansMembershipTable.IAmAliveTime%TYPE,
    VersionArg OrleansMembershipVersionTable.Version%TYPE)
RETURNS TABLE(row_count integer) AS
$func$
DECLARE
    RowCountVar int := 0;
BEGIN
    BEGIN
        UPDATE OrleansMembershipVersionTable
        SET Timestamp = now(), Version = Version + 1
        WHERE DeploymentId = DeploymentIdArg AND Version = VersionArg AND Version < 2147483647;
        GET DIAGNOSTICS RowCountVar = ROW_COUNT;

        INSERT INTO OrleansMembershipTable
            (DeploymentId, Address, Port, Generation, SiloName, HostName, Status, ProxyPort, StartTime, IAmAliveTime)
        SELECT DeploymentIdArg, AddressArg, PortArg, GenerationArg, SiloNameArg, HostNameArg,
            StatusArg, ProxyPortArg, StartTimeArg, IAmAliveTimeArg
        WHERE RowCountVar > 0
        ON CONFLICT (DeploymentId, Address, Port, Generation) DO NOTHING;
        GET DIAGNOSTICS RowCountVar = ROW_COUNT;

        IF RowCountVar = 0 THEN
            RAISE EXCEPTION 'no rows affected, rollback' USING ERRCODE = 'assert_failure';
        END IF;
        RETURN QUERY SELECT RowCountVar;
    EXCEPTION WHEN assert_failure THEN
        RETURN QUERY SELECT RowCountVar;
    END;
END
$func$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION update_membership(
    DeploymentIdArg OrleansMembershipTable.DeploymentId%TYPE,
    AddressArg OrleansMembershipTable.Address%TYPE,
    PortArg OrleansMembershipTable.Port%TYPE,
    GenerationArg OrleansMembershipTable.Generation%TYPE,
    StatusArg OrleansMembershipTable.Status%TYPE,
    SuspectTimesArg OrleansMembershipTable.SuspectTimes%TYPE,
    IAmAliveTimeArg OrleansMembershipTable.IAmAliveTime%TYPE,
    VersionArg OrleansMembershipVersionTable.Version%TYPE)
RETURNS TABLE(row_count integer) AS
$func$
DECLARE
    RowCountVar int := 0;
BEGIN
    BEGIN
        UPDATE OrleansMembershipVersionTable
        SET Timestamp = now(), Version = Version + 1
        WHERE DeploymentId = DeploymentIdArg AND Version = VersionArg AND Version < 2147483647;
        GET DIAGNOSTICS RowCountVar = ROW_COUNT;

        UPDATE OrleansMembershipTable
        SET Status = StatusArg, SuspectTimes = SuspectTimesArg,
            IAmAliveTime = GREATEST(IAmAliveTime, IAmAliveTimeArg)
        WHERE DeploymentId = DeploymentIdArg AND Address = AddressArg
            AND Port = PortArg AND Generation = GenerationArg AND RowCountVar > 0;
        GET DIAGNOSTICS RowCountVar = ROW_COUNT;

        IF RowCountVar = 0 THEN
            RAISE EXCEPTION 'no rows affected, rollback' USING ERRCODE = 'assert_failure';
        END IF;
        RETURN QUERY SELECT RowCountVar;
    EXCEPTION WHEN assert_failure THEN
        RETURN QUERY SELECT RowCountVar;
    END;
END
$func$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION cleanup_defunct_silo_entry(
    DeploymentIdArg OrleansMembershipTable.DeploymentId%TYPE,
    AddressArg OrleansMembershipTable.Address%TYPE,
    PortArg OrleansMembershipTable.Port%TYPE,
    GenerationArg OrleansMembershipTable.Generation%TYPE,
    VersionArg OrleansMembershipVersionTable.Version%TYPE,
    IAmAliveTimeArg OrleansMembershipTable.IAmAliveTime%TYPE,
    StartTimeArg OrleansMembershipTable.StartTime%TYPE,
    SuspectTimesArg OrleansMembershipTable.SuspectTimes%TYPE)
RETURNS TABLE(row_count integer) AS
$func$
DECLARE
    RowCountVar int := 0;
BEGIN
    BEGIN
        UPDATE OrleansMembershipVersionTable
        SET Timestamp = now(), Version = Version + 1
        WHERE DeploymentId = DeploymentIdArg AND Version = VersionArg AND Version < 2147483647;
        GET DIAGNOSTICS RowCountVar = ROW_COUNT;

        DELETE FROM OrleansMembershipTable
        WHERE DeploymentId = DeploymentIdArg AND Status = 6
            AND Address = AddressArg AND Port = PortArg AND Generation = GenerationArg
            AND IAmAliveTime = IAmAliveTimeArg AND StartTime = StartTimeArg
            AND COALESCE(SuspectTimes, '') = COALESCE(SuspectTimesArg, '')
            AND RowCountVar > 0;
        GET DIAGNOSTICS RowCountVar = ROW_COUNT;
        IF RowCountVar = 0 THEN
            RAISE EXCEPTION 'no rows affected, rollback' USING ERRCODE = 'assert_failure';
        END IF;
        RETURN QUERY SELECT RowCountVar;
    EXCEPTION WHEN assert_failure THEN
        RETURN QUERY SELECT RowCountVar;
    END;
END
$func$ LANGUAGE plpgsql;

UPDATE OrleansQuery SET QueryText = '
SELECT * FROM cleanup_defunct_silo_entry(@DeploymentId, @Address, @Port, @Generation,
    @Version, @IAmAliveTime, @StartTime, @SuspectTimes);'
WHERE QueryKey = 'CleanupDefunctSiloEntriesKey';

COMMIT;
