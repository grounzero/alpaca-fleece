#!/bin/bash
# Validation script for Alpaca trading bot critical fixes
# Run from project root: bash validate_fixes.sh

set -e

echo "=========================================="
echo "Alpaca Bot - Critical Fixes Validation"
echo "=========================================="
echo ""

# Color codes
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

ERRORS=0

# Helper functions
check_file() {
    if [ -f "$1" ]; then
        echo -e "${GREEN}✓${NC} File exists: $1"
    else
        echo -e "${RED}✗${NC} File missing: $1"
        ERRORS=$((ERRORS + 1))
    fi
}

check_syntax() {
    echo -n "  Checking syntax: $1 ... "
    if python3.12 -m py_compile "$1" 2>/dev/null; then
        echo -e "${GREEN}OK${NC}"
    else
        echo -e "${RED}FAILED${NC}"
        ERRORS=$((ERRORS + 1))
    fi
}

echo "1. Reconciliation Error - RESOLVED"
echo "=================================="
if [ ! -f "data/reconciliation_error.json" ]; then
    echo -e "${GREEN}✓${NC} Reconciliation error file cleared"
else
    echo -e "${RED}✗${NC} Reconciliation error file still exists"
    ERRORS=$((ERRORS + 1))
fi
echo ""

echo "2. Type Safety - Files Modified"
echo "=============================="
check_file "src/config.py"
check_syntax "src/config.py"

check_file "src/alpaca_api/base.py"
check_syntax "src/alpaca_api/base.py"

check_file "src/broker.py"
check_syntax "src/broker.py"

check_file "src/order_manager.py"
check_syntax "src/order_manager.py"

check_file "src/state_store.py"
check_syntax "src/state_store.py"

check_file "src/data/bars.py"
check_syntax "src/data/bars.py"

check_file "src/notifier.py"
check_syntax "src/notifier.py"
echo ""

echo "3. Pre-commit Configuration"
echo "=========================="
check_file ".pre-commit-config.yaml"

if grep -q "repo: https://github.com/astral-sh/ruff-pre-commit" .pre-commit-config.yaml; then
    echo -e "${GREEN}✓${NC} ruff hook configured"
else
    echo -e "${RED}✗${NC} ruff hook missing"
    ERRORS=$((ERRORS + 1))
fi

if grep -q "repo: https://github.com/psf/black" .pre-commit-config.yaml; then
    echo -e "${GREEN}✓${NC} black hook configured"
else
    echo -e "${RED}✗${NC} black hook missing"
    ERRORS=$((ERRORS + 1))
fi

if grep -q "repo: https://github.com/PyCQA/isort" .pre-commit-config.yaml; then
    echo -e "${GREEN}✓${NC} isort hook configured"
else
    echo -e "${RED}✗${NC} isort hook missing"
    ERRORS=$((ERRORS + 1))
fi

if grep -q "repo: https://github.com/pre-commit/mirrors-mypy" .pre-commit-config.yaml; then
    echo -e "${GREEN}✓${NC} mypy hook configured"
else
    echo -e "${RED}✗${NC} mypy hook missing"
    ERRORS=$((ERRORS + 1))
fi
echo ""

echo "4. CI/CD Pipeline"
echo "================="
check_file ".github/workflows/test.yml"

if grep -q "ruff check src tests" .github/workflows/test.yml; then
    echo -e "${GREEN}✓${NC} ruff check in CI"
else
    echo -e "${RED}✗${NC} ruff check missing in CI"
    ERRORS=$((ERRORS + 1))
fi

if grep -q "mypy src --strict" .github/workflows/test.yml; then
    echo -e "${GREEN}✓${NC} mypy check in CI"
else
    echo -e "${RED}✗${NC} mypy check missing in CI"
    ERRORS=$((ERRORS + 1))
fi

if grep -q "cov-fail-under=80" .github/workflows/test.yml; then
    echo -e "${GREEN}✓${NC} coverage threshold (80%) in CI"
else
    echo -e "${RED}✗${NC} coverage threshold missing in CI"
    ERRORS=$((ERRORS + 1))
fi
echo ""

echo "5. Database Improvements"
echo "======================="
# Check if NUMERIC is used instead of REAL
if grep -q "qty NUMERIC(10, 4)" src/state_store.py; then
    echo -e "${GREEN}✓${NC} qty uses NUMERIC(10, 4) for precision"
else
    echo -e "${RED}✗${NC} qty still uses REAL"
    ERRORS=$((ERRORS + 1))
fi

if grep -q "CREATE INDEX" src/state_store.py; then
    INDEX_COUNT=$(grep -c "CREATE INDEX" src/state_store.py)
    echo -e "${GREEN}✓${NC} $INDEX_COUNT indexes created"
    if [ "$INDEX_COUNT" -ge 6 ]; then
        echo -e "${GREEN}✓${NC} Minimum 6 indexes found"
    else
        echo -e "${YELLOW}⚠${NC} Expected at least 6 indexes, found $INDEX_COUNT"
        ERRORS=$((ERRORS + 1))
    fi
else
    echo -e "${RED}✗${NC} No indexes found in schema"
    ERRORS=$((ERRORS + 1))
fi
echo ""

echo "6. Error Handling Improvements"
echo "=============================="
if grep -q "except ValueError" src/data/bars.py; then
    echo -e "${GREEN}✓${NC} Specific ValueError handling in bars.py"
else
    echo -e "${RED}✗${NC} Specific error handling missing in bars.py"
    ERRORS=$((ERRORS + 1))
fi

if grep -q "except smtplib.SMTPAuthenticationError" src/notifier.py; then
    echo -e "${GREEN}✓${NC} SMTP authentication error handling in notifier.py"
else
    echo -e "${RED}✗${NC} SMTP error handling missing in notifier.py"
    ERRORS=$((ERRORS + 1))
fi

if grep -q "SMTP_PASSWORD" src/notifier.py && ! grep -q 'logger.error.*SMTP_PASSWORD' src/notifier.py; then
    echo -e "${GREEN}✓${NC} SMTP credentials not logged in error messages"
else
    echo -e "${RED}✗${NC} SMTP credentials may be logged"
    ERRORS=$((ERRORS + 1))
fi
echo ""

echo "7. Documentation"
echo "================"
if grep -q "Provides low-level access" src/alpaca_api/base.py; then
    echo -e "${GREEN}✓${NC} Module docstring added to base.py"
else
    echo -e "${RED}✗${NC} Module docstring missing from base.py"
    ERRORS=$((ERRORS + 1))
fi

if grep -q "def __init__.*-> None:" src/broker.py; then
    echo -e "${GREEN}✓${NC} Return type hints on methods in broker.py"
else
    echo -e "${RED}✗${NC} Return type hints missing from broker.py"
    ERRORS=$((ERRORS + 1))
fi
echo ""

echo "8. Configuration Updates"
echo "======================="
if grep -q "mypy>=" pyproject.toml; then
    echo -e "${GREEN}✓${NC} mypy added to dev dependencies"
else
    echo -e "${RED}✗${NC} mypy missing from dev dependencies"
    ERRORS=$((ERRORS + 1))
fi

if grep -q "strict = true" pyproject.toml; then
    echo -e "${GREEN}✓${NC} mypy strict mode configured"
else
    echo -e "${RED}✗${NC} mypy strict mode not configured"
    ERRORS=$((ERRORS + 1))
fi
echo ""

echo "=========================================="
echo "Validation Summary"
echo "=========================================="

if [ $ERRORS -eq 0 ]; then
    echo -e "${GREEN}✓ All critical fixes validated successfully!${NC}"
    echo ""
    echo "Next steps:"
    echo "  1. Install pre-commit: pre-commit install"
    echo "  2. Run pre-commit: pre-commit run --all-files"
    echo "  3. Run tests: pytest tests -v --cov=src --cov-fail-under=80"
    echo "  4. Start bot: python main.py"
    exit 0
else
    echo -e "${RED}✗ Validation failed with $ERRORS error(s)${NC}"
    exit 1
fi
