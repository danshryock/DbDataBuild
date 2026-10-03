-- description: Column names are lower case with underscores
-- severity: warning
-- tags: naming
SELECT model, column_name FROM metadata_columns WHERE kind = 'model' AND column_name <> lower(column_name)
