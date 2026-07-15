# ISAS — minimal dev targets (P0.2). See AGENTS.md / DEPLOYMENT.md for the full flow.
# Covers the .NET solution only; AIService (Python) uses pytest — see `make ai-test`.

SLN := isas-server.sln
AI_DIR := src/services/Isas.AIService

.DEFAULT_GOAL := check

.PHONY: setup build test check ai-test clean

setup:            ## Restore NuGet packages for the solution
	dotnet restore $(SLN)

build: setup      ## Build the solution (Release-agnostic; static/layer-1)
	dotnet build $(SLN) --no-restore

test:             ## Run all .NET test projects (Auth · Interview · Campaign · Payment)
	dotnet test $(SLN)

check: build test ## Build then test — the "clean state" gate before a commit

ai-test:          ## Run AIService pytest (needs: pip install -r $(AI_DIR)/requirements-dev.txt)
	cd $(AI_DIR) && python -m pytest

clean:            ## Remove build output
	dotnet clean $(SLN)
