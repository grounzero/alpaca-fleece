"""Unit tests for symbol batching in stream.py."""

import pytest
from src.stream import batch_iter


class TestBatchIter:
    """Tests for batch_iter utility function."""
    
    def test_batch_iter_even_division(self):
        """Test batching with evenly divisible items."""
        items = list(range(10))  # [0,1,2,3,4,5,6,7,8,9]
        result = list(batch_iter(items, 2))
        
        assert len(result) == 5
        assert result[0] == [0, 1]
        assert result[1] == [2, 3]
        assert result[4] == [8, 9]
    
    def test_batch_iter_uneven_division(self):
        """Test batching with non-evenly divisible items."""
        items = list(range(11))  # [0,1,2,...,10]
        result = list(batch_iter(items, 3))
        
        assert len(result) == 4
        assert result[0] == [0, 1, 2]
        assert result[1] == [3, 4, 5]
        assert result[2] == [6, 7, 8]
        assert result[3] == [9, 10]  # Last batch has only 2 items
    
    def test_batch_iter_single_item_batches(self):
        """Test batching with batch size of 1."""
        items = [1, 2, 3]
        result = list(batch_iter(items, 1))
        
        assert len(result) == 3
        assert result[0] == [1]
        assert result[1] == [2]
        assert result[2] == [3]
    
    def test_batch_iter_larger_batch_than_items(self):
        """Test batching when batch size > item count."""
        items = [1, 2, 3]
        result = list(batch_iter(items, 10))
        
        assert len(result) == 1
        assert result[0] == [1, 2, 3]
    
    def test_batch_iter_empty_list(self):
        """Test batching with empty list."""
        items = []
        result = list(batch_iter(items, 5))
        
        assert len(result) == 0
    
    def test_batch_iter_strings(self):
        """Test batching with string symbols."""
        symbols = ["AAPL", "MSFT", "GOOGL", "NVDA", "TSLA"]
        result = list(batch_iter(symbols, 2))
        
        assert len(result) == 3
        assert result[0] == ["AAPL", "MSFT"]
        assert result[1] == ["GOOGL", "NVDA"]
        assert result[2] == ["TSLA"]
    
    def test_batch_iter_31_symbols_batch_10(self):
        """Test batching 31 symbols (realistic trading scenario) into batches of 10."""
        symbols = [f"SYM{i:02d}" for i in range(31)]
        result = list(batch_iter(symbols, 10))
        
        # 31 symbols ÷ 10 per batch = 4 batches (3 full + 1 partial)
        assert len(result) == 4
        assert len(result[0]) == 10
        assert len(result[1]) == 10
        assert len(result[2]) == 10
        assert len(result[3]) == 1  # Last batch has 1 symbol
    
    def test_batch_iter_preserves_order(self):
        """Test that batching preserves item order."""
        items = ["A", "B", "C", "D", "E", "F"]
        result = list(batch_iter(items, 2))
        
        # Flatten and compare
        flattened = [item for batch in result for item in batch]
        assert flattened == items
    
    def test_batch_iter_large_dataset(self):
        """Test batching with large dataset."""
        items = list(range(1000))
        result = list(batch_iter(items, 100))
        
        # 1000 items ÷ 100 = 10 batches
        assert len(result) == 10
        
        # All batches should have 100 items
        for batch in result:
            assert len(batch) == 100


class TestSymbolBatchingIntegration:
    """Integration tests for symbol batching logic."""
    
    def test_batch_subscription_order(self):
        """Verify batches are created in correct order for subscription."""
        symbols = ["AAPL", "MSFT", "GOOGL", "NVDA", "TSLA", "AMD"]
        batch_size = 2
        
        batches = list(batch_iter(symbols, batch_size))
        
        # First batch should be first symbols in order
        assert batches[0] == ["AAPL", "MSFT"]
        assert batches[1] == ["GOOGL", "NVDA"]
        assert batches[2] == ["TSLA", "AMD"]
    
    def test_realistic_31_symbols_with_10_batch(self):
        """Test realistic scenario: 31 trading symbols, 10 per batch."""
        symbols = [
            "AAPL", "MSFT", "GOOGL", "NVDA", "QQQ", "SPY", "TSLA", "AMD", 
            "AMZN", "META", "NFLX", "UBER", "COIN", "MSTR", "ARKK", "IWM", 
            "EEM", "GLD", "TLT", "USO", "RTX", "LMT", "NOC", "BA", "GD", 
            "GOLD", "SLV", "PAAS", "HL", "SCCO", "FCX"
        ]
        
        batches = list(batch_iter(symbols, 10))
        
        # Expected: 4 batches (3×10 + 1×1)
        assert len(batches) == 4
        
        # Batch 1-3 should have 10 items
        assert len(batches[0]) == 10
        assert len(batches[1]) == 10
        assert len(batches[2]) == 10
        
        # Batch 4 should have 1 item
        assert len(batches[3]) == 1
        
        # First batch should start with tech stocks
        assert batches[0][0] == "AAPL"
        assert batches[0][-1] == "META"
        
        # Last batch should have the last symbol
        assert batches[3][0] == "FCX"
    
    def test_no_duplicate_symbols_after_batching(self):
        """Verify no symbols are duplicated after batching."""
        symbols = list(range(50))
        batches = list(batch_iter(symbols, 7))
        
        # Flatten batches and count occurrences
        flattened = [item for batch in batches for item in batch]
        
        assert len(flattened) == len(set(flattened))  # All unique
        assert sorted(flattened) == sorted(symbols)


class TestBatchingEdgeCases:
    """Edge case tests for batching."""
    
    def test_batch_size_zero_creates_empty_result(self):
        """Test that batch size 0 returns empty list (Python islice behaviour)."""
        items = [1, 2, 3]
        
        # batch_size = 0 will cause islice to create empty batches
        # This results in an empty iterator (Python behaviour)
        result = list(batch_iter(items, 0))
        assert result == []
    
    def test_negative_batch_size(self):
        """Test negative batch size (should be treated as 0)."""
        items = [1, 2, 3]
        
        # Negative batch size in islice is treated as 0
        with pytest.raises((ValueError, StopIteration)):
            list(batch_iter(items, -1))
    
    def test_batch_generator_is_lazy(self):
        """Test that batch_iter returns a generator (lazy evaluation)."""
        items = range(100)
        batch_gen = batch_iter(items, 10)
        
        # Should be a generator
        assert hasattr(batch_gen, '__iter__')
        assert hasattr(batch_gen, '__next__')
        
        # Getting first batch shouldn't process all items
        first_batch = next(batch_gen)
        assert first_batch == [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]


class TestBatchingMetrics:
    """Test batching metrics and calculations."""
    
    def test_batch_count_calculation(self):
        """Verify formula for calculating batch count."""
        def expected_batch_count(total_items, batch_size):
            return (total_items - 1) // batch_size + 1
        
        # Test cases
        test_cases = [
            (10, 5, 2),      # 10÷5 = 2
            (11, 5, 3),      # 11÷5 = 2.2 → 3
            (31, 10, 4),     # 31÷10 = 3.1 → 4
            (100, 25, 4),    # 100÷25 = 4
            (1, 10, 1),      # 1÷10 = 0.1 → 1
        ]
        
        for total, batch_size, expected in test_cases:
            actual = len(list(batch_iter(range(total), batch_size)))
            expected_calc = expected_batch_count(total, batch_size)
            assert actual == expected
            assert expected_calc == expected
    
    def test_total_items_preserved_after_batching(self):
        """Verify all items are present after batching."""
        for total in [1, 10, 31, 50, 100]:
            for batch_size in [2, 5, 10]:
                items = list(range(total))
                batches = list(batch_iter(items, batch_size))
                flattened = [item for batch in batches for item in batch]
                
                assert len(flattened) == total
                assert flattened == items


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
