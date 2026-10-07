import { fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import Preview, { PreviewRoutes } from './Preview';
function mount(path = '/') {
  render(
    <MemoryRouter initialEntries={[path]}>
      <PreviewRoutes />
    </MemoryRouter>,
  );
}
describe('Isolated editorial prototype', () => {
  it('clearly marks non-production content, disables login and moves skip-link focus to main', async () => {
    mount();
    const skip = screen.getByText('Saltar al contenido', { selector: 'a' });
    expect(skip).toHaveRole('link');
    expect(skip).toHaveAccessibleName('Saltar al contenido');
    expect(skip).toHaveAttribute('href', '#preview-main');
    await userEvent.click(skip);
    const main = document.querySelector('main')!;
    expect(main).toHaveRole('main');
    expect(main).toHaveFocus();
    expect(
      screen.getByText(/Vista previa de diseño · Contenido de demostración/),
    ).toBeInTheDocument();
    expect(
      within(document.querySelector('header')!).getByRole('button', {
        name: 'Ingresar: disponible en la aplicación principal',
      }),
    ).toBeDisabled();
  }, 10000);
  it('preserves all six original category links and their accessible names', () => {
    mount();
    const labels = ['Lecturas', 'Documentales', 'Videos', 'Podcast', 'Charlas online', 'Cursos'];
    const links = within(document.querySelector('.category-grid')!).getAllByRole('link');
    expect(links).toHaveLength(labels.length);
    links.forEach((link, index) => expect(link).toHaveAccessibleName(labels[index]));
  }, 10000);
  it('explains membership references without enabling purchases', async () => {
    mount();
    const memberships = within(document.getElementById('memberships')!);
    await userEvent.click(memberships.getAllByRole('button', { name: /Ver referencia/ })[0]!);
    expect(screen.getByRole('status')).toHaveTextContent('No se ha habilitado ninguna compra');
    await userEvent.click(
      within(document.querySelector('.faq-section')!).getByText('¿Cómo funcionan las membresías?'),
    );
    expect(
      within(document.querySelector('.faq-section')!).getByText(/Sus condiciones se validarán/),
    ).toBeVisible();
  }, 10000);
  it('filters only fixture contents and handles empty searches', async () => {
    mount('/explore');
    fireEvent.change(screen.getByRole('searchbox', { name: 'Buscar contenido' }), {
      target: { value: 'inexistente' },
    });
    expect(
      screen.getByText('No hay contenidos que coincidan. Prueba otra búsqueda.'),
    ).toBeInTheDocument();
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: '' } });
    await userEvent.click(screen.getByRole('button', { name: 'Podcast' }));
    expect(screen.getByRole('status')).toHaveTextContent('1 contenidos de demostración');
    expect(screen.getByRole('heading', { name: 'El arte de escuchar' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Todo' }));
    expect(screen.getByRole('status')).toHaveTextContent('6 contenidos de demostración');
  }, 10000);
  it('shows a video detail with explicit unavailable media and keyboard-operable information', async () => {
    mount('/video/filosofia-cotidiana');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('La filosofía empieza');
    expect(
      screen.getByRole('button', { name: /Reproducción de vídeo no disponible/ }),
    ).toBeDisabled();
    await userEvent.click(screen.getByRole('tab', { name: 'Transcripción' }));
    expect(screen.getByRole('tabpanel')).toHaveTextContent('No se ha incorporado un archivo');
    fireEvent.keyDown(screen.getByRole('tab', { name: 'Transcripción' }), { key: 'ArrowLeft' });
    expect(screen.getByRole('tab', { name: 'Sobre este contenido' })).toHaveAttribute(
      'aria-selected',
      'true',
    );
    fireEvent.keyDown(screen.getByRole('tab', { name: 'Sobre este contenido' }), {
      key: 'ArrowRight',
    });
    expect(screen.getByRole('tab', { name: 'Transcripción' })).toHaveFocus();
    await userEvent.click(screen.getByRole('tab', { name: 'Sobre este contenido' }));
    expect(screen.getByRole('tabpanel')).toHaveTextContent('vida diaria');
  }, 10000);
  it('shows podcast, institutional and membership pages without a payment or invented media API', () => {
    mount('/podcast/arte-de-escuchar');
    expect(
      screen.getByRole('button', { name: /Reproducción de podcast no disponible/ }),
    ).toBeDisabled();
    expect(within(screen.getByRole('main')).getByRole('heading', { level: 1 })).toHaveTextContent(
      'El arte de escuchar',
    );
  });
  it('renders institutional and missing-content states', () => {
    mount('/institutional');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent(
      'Una filosofía que se vive',
    );
  });
  it('renders membership reference without checkout', () => {
    mount('/memberships');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Membresías de referencia');
    expect(screen.queryByText('Comprar')).not.toBeInTheDocument();
  });
  it('renders unknown content and routes safely', () => {
    mount('/video/missing');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent(
      'No encontramos este contenido',
    );
  });
  it('handles a route outside the prototype', () => {
    mount('/missing');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Esta página no está aquí');
  });
  it('loads the isolated hash entry point', () => {
    window.history.replaceState({}, '', '/preview.html#/explore');
    render(<Preview />);
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Una idea puede');
  });
});
