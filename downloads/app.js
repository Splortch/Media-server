'use strict';
const filters = document.querySelector('.filters');
const buttons = [...document.querySelectorAll('[data-filter]')];
const sections = [...document.querySelectorAll('.category')];
const status = document.getElementById('filter-status');
if (filters && status) {
  filters.hidden = false;
  buttons.forEach(button => button.addEventListener('click', () => {
    const selected = button.dataset.filter;
    buttons.forEach(item => item.setAttribute('aria-pressed', String(item === button)));
    sections.forEach(section => { section.hidden = selected !== 'all' && section.id !== selected; });
    status.textContent = selected === 'all' ? 'Showing all platforms.' : `Showing ${selected}.`;
  }));
}
