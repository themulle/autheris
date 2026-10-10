"""Spark SQL stand-in for Databricks SQL (CI proxy for the governed AST compiler, WP-C4).

A long-lived process: reads one JSON request per line from stdin, writes one JSON response per line to stdout.

Requests:
  {"op": "ddl", "sql": "..."}                                       run a statement without parameters (fixture setup)
  {"op": "query", "sql": "...", "params": [{"name": "p1", "type": "string", "value": "x"}, ...]}

Parameters use Spark named parameter markers (:name), the same syntax as Databricks SQL. ANSI mode is on (matching Databricks SQL
warehouses) and variable substitution is on, which is the worst case for SEC-ADG-10.
"""
import base64
import datetime
import decimal
import json
import glob
import os
import sys

# The official image ships pyspark under SPARK_HOME without putting it on the Python path.
spark_home = os.environ.get("SPARK_HOME", "/opt/spark")
sys.path.insert(0, os.path.join(spark_home, "python"))
for archive in glob.glob(os.path.join(spark_home, "python", "lib", "py4j-*.zip")):
    sys.path.insert(0, archive)

from pyspark.sql import SparkSession  # noqa: E402

builder = (SparkSession.builder.master("local[2]")
           .appName("autheris-ast-gate")
           .config("spark.sql.ansi.enabled", "true")
           .config("spark.sql.variable.substitute", "true")
           .config("spark.sql.shuffle.partitions", "2")
           .config("spark.ui.enabled", "false")
           .config("spark.sql.warehouse.dir", "/tmp/spark-warehouse"))
if os.environ.get("AUTHERIS_SPARK_DELTA") == "1":
    # WP-A7: UPDATE, DELETE and MERGE need Delta (the jars are baked into the derived image, see Dockerfile).
    builder = (builder.config("spark.sql.extensions", "io.delta.sql.DeltaSparkSessionExtension")
               .config("spark.sql.catalog.spark_catalog", "org.apache.spark.sql.delta.catalog.DeltaCatalog")
               # open-source Delta rejects collated string columns; Databricks Delta supports them, so the proxy opts out of the check
               # to exercise the case-insensitive tenant column (B-1).
               .config("spark.databricks.delta.schema.typeCheck.enabled", "false"))
spark = builder.getOrCreate()
spark.sparkContext.setLogLevel("ERROR")


def convert(parameter):
    kind, value = parameter["type"], parameter.get("value")
    if value is None:
        return None
    if kind == "string":
        return value
    if kind in ("int32", "int64"):
        return int(value)
    if kind == "decimal":
        return decimal.Decimal(value)
    if kind == "double":
        return float(value)
    if kind == "boolean":
        return bool(value)
    if kind == "date":
        return datetime.date.fromisoformat(value)
    if kind == "timestamp":
        return datetime.datetime.fromisoformat(value)
    if kind == "binary":
        return bytearray(base64.b64decode(value))
    raise ValueError("unsupported parameter type " + kind)


def jsonable(value):
    if value is None or isinstance(value, (bool, int, float, str)):
        return value
    if isinstance(value, (bytes, bytearray)):
        return base64.b64encode(bytes(value)).decode("ascii")
    return str(value)


for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    try:
        request = json.loads(line)
        if request["op"] == "ddl":
            spark.sql(request["sql"]).collect()
            response = {"ok": True}
        else:
            args = {p["name"]: convert(p) for p in request.get("params", [])}
            frame = spark.sql(request["sql"], args=args)
            rows = [[jsonable(v) for v in row] for row in frame.collect()]
            response = {"ok": True, "columns": frame.columns, "rows": rows}
    except Exception as exc:  # report the error class and first line only
        response = {"ok": False, "error": type(exc).__name__ + ": " + str(exc).splitlines()[0][:300]}
    sys.stdout.write(json.dumps(response) + "\n")
    sys.stdout.flush()
