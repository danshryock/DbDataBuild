-- description: Staging models are views, and they read only sources
-- tags: layers
SELECT m.model, m.kind_type, u.upstream
FROM metadata_models m
JOIN metadata_upstream u ON u.model = m.model
WHERE m.model LIKE 'staging.%' AND (m.kind_type <> 'view' OR u.upstream_kind <> 'source')
