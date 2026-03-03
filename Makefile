.PHONY: help start stop restart status daemon-start daemon-stop daemon-restart daemon-status systemd-install systemd-start systemd-stop systemd-status test test-py test-cs clean spawn-agent instantiate-agent list-agents build-cs run-cs

# Default target
help:
	@echo "Alpaca Trading Bot - Process Management"
	@echo ""
	@echo "Python Bot Commands:"
	@echo "  make start        - Start Python bot with setsid (survives disconnect)"
	@echo "  make stop         - Stop the Python bot"
	@echo "  make restart      - Restart the Python bot"
	@echo "  make status       - Check Python bot status"
	@echo "  make test-py      - Run Python tests"
	@echo ""
	@echo "C# Worker Commands:"
	@echo "  make build-cs     - Build C# worker"
	@echo "  make run-cs       - Run C# worker"
	@echo "  make test-cs      - Run C# tests"
	@echo ""
	@echo "Python daemon (double-fork):"
	@echo "  make daemon-start  - Start bot as daemon"
	@echo "  make daemon-stop   - Stop daemon"
	@echo "  make daemon-status - Check daemon status"
	@echo ""
	@echo "Systemd user service:"
	@echo "  make systemd-install   - Install systemd user service"
	@echo "  make systemd-start     - Start systemd service"
	@echo "  make systemd-stop      - Stop systemd service"
	@echo "  make systemd-status    - Check systemd service"
	@echo ""
	@echo "Agent spawning (with language standards):"
	@echo "  make spawn-agent TASK='description' - Spawn agent with British English rules"
	@echo ""
	@echo "Agent templates:"
	@echo "  make list-agents          - List available agent templates"
	@echo "  make instantiate-agent AGENT=python-dev - Create project agent from template"
	@echo ""
	@echo "Other:"
	@echo "  make test         - Run all tests (Python + C#)"
	@echo "  make clean        - Clean up"

# Shell-based (setsid) method - RECOMMENDED for simple use
start:
	cd py && ./bot.sh start

stop:
	cd py && ./bot.sh stop

restart:
	cd py && ./bot.sh restart

status:
	cd py && ./bot.sh status

# Python daemon method (double-fork technique)
daemon-start:
	cd py && .venv/bin/python daemon.py start

daemon-stop:
	cd py && .venv/bin/python daemon.py stop

daemon-restart:
	cd py && .venv/bin/python daemon.py restart

daemon-status:
	cd py && .venv/bin/python daemon.py status

# Systemd user service method (most robust)
systemd-install:
	@echo "Installing systemd user service..."
	mkdir -p ~/.config/systemd/user
	cp alpaca-bot.service ~/.config/systemd/user/
	systemctl --user daemon-reload
	@echo "Service installed. Use 'make systemd-start' to begin."

systemd-start:
	systemctl --user start alpaca-bot

systemd-stop:
	systemctl --user stop alpaca-bot

systemd-restart:
	systemctl --user restart alpaca-bot

systemd-status:
	systemctl --user status alpaca-bot

systemd-enable:
	systemctl --user enable alpaca-bot

systemd-disable:
	systemctl --user disable alpaca-bot

# Agent spawning with language standards
spawn-agent:
	@if [ -z "$(TASK)" ]; then \
		echo "Usage: make spawn-agent TASK='Your task description'"; \
		echo "Example: make spawn-agent TASK='Refactor order manager'"; \
		exit 1; \
	fi
	@./scripts/spawn-with-standards.sh "$(TASK)"

# Agent template instantiation
AGENTS_DIR ?= $(HOME)/.openclaw/agents/templates

list-agents:
	@python $(AGENTS_DIR)/instantiate-agent.py --list

instantiate-agent:
	@if [ -z "$(AGENT)" ]; then \
		echo "Usage: make instantiate-agent AGENT=python-dev"; \
		echo "Example: make instantiate-agent AGENT=business-analyst"; \
		echo ""; \
		echo "Available agents:"; \
		python $(AGENTS_DIR)/instantiate-agent.py --list; \
		exit 1; \
	fi
	@python $(AGENTS_DIR)/instantiate-agent.py $(AGENT)

# Development
test: test-py test-cs

test-py:
	cd py && .venv/bin/pytest tests/ -v

test-cs:
	cd cs && dotnet test --verbosity normal

build-cs:
	cd cs && dotnet build src/AlpacaFleece.Worker/AlpacaFleece.Worker.csproj

run-cs:
	cd cs && ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/AlpacaFleece.Worker/AlpacaFleece.Worker.csproj

clean:
	find py -type d -name __pycache__ -exec rm -rf {} + 2>/dev/null || true
	find py -type f -name "*.pyc" -delete 2>/dev/null || true
	find py -type f -name ".coverage" -delete 2>/dev/null || true
	rm -rf py/data/alpaca_bot.pid 2>/dev/null || true
	cd cs && dotnet clean 2>/dev/null || true

dev-setup:
	@echo "Creating virtualenv in py/.venv and installing Python development dependencies..."
	cd py && python3 -m venv .venv
	cd py && . .venv/bin/activate && python -m pip install --upgrade pip setuptools wheel
	@if [ -f py/requirements-dev.txt ]; then \
		cd py && . .venv/bin/activate && pip install -r requirements-dev.txt; \
	fi
	cd py && . .venv/bin/activate && pip install pre-commit || true
	# Install pre-commit hooks into the default .git/hooks directory
	cd py && . .venv/bin/activate && pre-commit install || true
	cd py && . .venv/bin/activate && pre-commit install --hook-type pre-push || true
	@echo "Python setup done. Activate with: cd py && source .venv/bin/activate"
	@echo ""
	@echo "Restoring C# packages..."
	cd cs && dotnet restore
	@echo "C# setup done."
