# ==============================================================================
# Makefile for GqlGateway Benchmark & Simulation Suite
# ==============================================================================

.PHONY: all bench bench-minimal bench-lakehouse bench-azure bench-enterprise bench-om-real bench-relational bench-full up-om-real test-ext up down chaos report clean test

all: bench

# Run full end-to-end benchmark suite (default: full)
bench:
	@chmod +x run-benchmark.sh scripts/*.sh scripts/*.py 2>/dev/null || true
	./run-benchmark.sh --scenario $(or $(SCENARIO),full)

# Memory-optimized scenarios
bench-minimal:
	./run-benchmark.sh --scenario minimal

bench-lakehouse:
	./run-benchmark.sh --scenario lakehouse

bench-azure:
	./run-benchmark.sh --scenario azure

bench-enterprise:
	./run-benchmark.sh --scenario enterprise

bench-om-real:
	./run-benchmark.sh --scenario openmetadata-real

bench-relational:
	./run-benchmark.sh --scenario relational

bench-full:
	./run-benchmark.sh --scenario full

# Start real OpenMetadata and OpenSearch stack alongside main infrastructure
up-om-real:
	podman compose -f podman-compose.yaml -f podman-compose.openmetadata.yaml up -d postgres opensearch openmetadata-server

# Run extensions verification test directly
test-ext:
	python3 scripts/test_extensions.py

# Start infrastructure containers and wait for healthchecks
up:
	@./scripts/stage_sources.sh
	podman compose up -d --build postgres redis governance-seed gqlgateway-api reverse-proxy prometheus grafana || \
	podman-compose up -d --build postgres redis governance-seed gqlgateway-api reverse-proxy prometheus grafana

# Tear down containers and networks
down:
	podman compose down || podman-compose down

# Execute Redis outage chaos test against running environment
chaos:
	python3 scripts/chaos_test.py

# Re-generate Markdown and CSV report from recorded metrics
report:
	python3 scripts/generate_report.py

# Clean generated reports and staged build cache
clean:
	rm -rf results/*.json results/*.csv results/*.md src_build/
