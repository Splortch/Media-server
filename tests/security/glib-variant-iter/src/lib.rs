#[cfg(test)]
mod tests {
    use glib::variant::ToVariant;

    // Exercise the optimized FFI out-pointer path from RUSTSEC-2024-0429.
    // These tests must run in --release: debug-only checks can miss this UB.
    #[test]
    fn forward_reverse_and_mixed_string_iteration() {
        let values = ["", "hello", "åäö", "media-server"];
        let variant = values.to_variant();
        assert_eq!(variant.array_iter_str().unwrap().collect::<Vec<_>>(), values);
        assert_eq!(
            variant.array_iter_str().unwrap().rev().collect::<Vec<_>>(),
            values.iter().copied().rev().collect::<Vec<_>>()
        );
        let mut mixed = variant.array_iter_str().unwrap();
        assert_eq!(mixed.next(), Some(""));
        assert_eq!(mixed.next_back(), Some("media-server"));
        assert_eq!(mixed.next(), Some("hello"));
        assert_eq!(mixed.next_back(), Some("åäö"));
        assert_eq!(mixed.next(), None);
    }

    #[test]
    fn nth_last_and_empty_arrays() {
        let variant = ["first", "middle", "last"].to_variant();
        assert_eq!(variant.array_iter_str().unwrap().nth(1), Some("middle"));
        assert_eq!(variant.array_iter_str().unwrap().nth_back(1), Some("middle"));
        assert_eq!(variant.array_iter_str().unwrap().last(), Some("last"));
        let empty = Vec::<String>::new().to_variant();
        assert_eq!(empty.array_iter_str().unwrap().next(), None);
        assert_eq!(empty.array_iter_str().unwrap().next_back(), None);
    }
}
